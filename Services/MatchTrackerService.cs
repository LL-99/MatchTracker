using Microsoft.JSInterop;
using Newtonsoft.Json;
using System.ComponentModel;

namespace MatchTracker.Services;

public sealed class MatchTrackerService
{
    private const string LocalStorageKey = "matchtracker.state.v1";
    private const int TournamentHistoryLimit = 100;
    private readonly object _sync = new();
    private MatchTrackerState _state = new();
    private bool _isInitialized;
    public event Action? OptionsChanged;

    public async Task EnsureInitializedAsync(IJSRuntime jsRuntime)
    {
        lock (_sync)
        {
            if (_isInitialized)
            {
                return;
            }
        }

        try
        {
            var serializedState = await jsRuntime.InvokeAsync<string?>("localStorage.getItem", LocalStorageKey);
            if (!TryDeserializeState(serializedState, out var loadedState))
            {
                loadedState = new MatchTrackerState();
            }

            lock (_sync)
            {
                if (_isInitialized)
                {
                    return;
                }

                _state = loadedState;
                _isInitialized = true;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("Error during data loading: " + ex);
        }
    }

    public async Task PersistAsync(IJSRuntime jsRuntime)
    {
        var snapshot = CreateSnapshot();
        var serializedState = JsonConvert.SerializeObject(snapshot, Formatting.None);
        await jsRuntime.InvokeVoidAsync("localStorage.setItem", LocalStorageKey, serializedState);
    }

    public string ExportStateJson()
    {
        var snapshot = CreateSnapshot();
        return JsonConvert.SerializeObject(snapshot, Formatting.Indented);
    }

    public bool TryImportStateJson(string? serializedState, out string errorMessage)
    {
        if (string.IsNullOrWhiteSpace(serializedState))
        {
            errorMessage = "Import file is empty.";
            return false;
        }

        if (!TryDeserializeState(serializedState, out var importedState))
        {
            errorMessage = "Invalid JSON file format.";
            return false;
        }

        lock (_sync)
        {
            _state = importedState;
            _isInitialized = true;
        }

        OptionsChanged?.Invoke();
        errorMessage = string.Empty;
        return true;
    }

    public bool IsPointsEnabled()
    {
        lock (_sync)
        {
            return _state.UsePoints;
        }
    }

    public bool SetPointsEnabled(bool isEnabled)
    {
        var changed = false;

        lock (_sync)
        {
            if (_state.UsePoints != isEnabled)
            {
                _state.UsePoints = isEnabled;
                changed = true;
            }
        }

        if (changed)
        {
            OptionsChanged?.Invoke();
        }

        return changed;
    }

    public IReadOnlyList<Player> GetPlayers()
    {
        return GetPlayers(includeInactive: false);
    }

    public IReadOnlyList<Player> GetPlayers(bool includeInactive)
    {
        lock (_sync)
        {
            return _state.Players
                .Where(player => includeInactive || player.Active)
                .OrderBy(player => player.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(player => player.Name, StringComparer.Ordinal)
                .ToList();
        }
    }

    public IReadOnlyList<MatchRecord> GetMatches()
    {
        lock (_sync)
        {
            return _state.Matches
                .OrderByDescending(match => match.PlayedOnUtc)
                .ToList();
        }
    }

    public TournamentSnapshot? GetCurrentTournamentSnapshot()
    {
        lock (_sync)
        {
            return _state.CurrentTournament is null
                ? null
                : CloneTournament(_state.CurrentTournament);
        }
    }

    public IReadOnlyList<TournamentHistorySummary> GetTournamentHistorySummaries()
    {
        lock (_sync)
        {
            return _state.TournamentHistory
                .OrderByDescending(summary => summary.ArchivedOnUtc)
                .ThenByDescending(summary => summary.GeneratedOnUtc)
                .Select(CloneTournamentHistorySummary)
                .ToList();
        }
    }

    public void SetCurrentTournamentSnapshot(TournamentSnapshot? tournamentSnapshot)
    {
        lock (_sync)
        {
            var nextTournament = tournamentSnapshot is null
                ? null
                : CloneTournament(tournamentSnapshot);

            nextTournament = SanitizeTournament(nextTournament);

            if (_state.CurrentTournament is not null)
            {
                var replacingCurrent = nextTournament is null
                    || nextTournament.TournamentId != _state.CurrentTournament.TournamentId;

                if (replacingCurrent)
                {
                    ArchiveTournamentLocked(_state.CurrentTournament, DateTime.UtcNow);
                }
            }

            _state.CurrentTournament = nextTournament;
            if (_state.CurrentTournament is not null)
            {
                ResolveKnockoutBracketLocked(_state.CurrentTournament);
                SyncTournamentMatchesLocked(_state.CurrentTournament);
                _state.Matches = _state.Matches
                    .OrderByDescending(match => match.PlayedOnUtc)
                    .ToList();
            }
        }
    }

    public bool TryDeleteArchivedTournament(Guid tournamentId, out int removedMatchCount, out string errorMessage)
    {
        removedMatchCount = 0;

        lock (_sync)
        {
            var removedHistoryEntries = _state.TournamentHistory.RemoveAll(summary => summary.TournamentId == tournamentId);
            if (removedHistoryEntries == 0)
            {
                errorMessage = "Archived tournament not found.";
                return false;
            }

            removedMatchCount = _state.Matches.RemoveAll(match => match.SourceTournamentId == tournamentId);
        }

        errorMessage = string.Empty;
        return true;
    }

    public bool TryDiscardCurrentTournament(out int removedMatchCount, out string errorMessage)
    {
        removedMatchCount = 0;

        lock (_sync)
        {
            if (_state.CurrentTournament is null)
            {
                errorMessage = "No active tournament found.";
                return false;
            }

            var tournamentId = _state.CurrentTournament.TournamentId;
            removedMatchCount = _state.Matches.RemoveAll(match => match.SourceTournamentId == tournamentId);
            _state.CurrentTournament = null;
        }

        errorMessage = string.Empty;
        return true;
    }

    public bool TryRecordTournamentMatchResult(
        Guid tournamentId,
        int roundNumber,
        Guid tournamentMatchId,
        MatchResult resultForPlayerA,
        out string errorMessage)
    {
        lock (_sync)
        {
            if (_state.CurrentTournament is null)
            {
                errorMessage = "No active tournament found.";
                return false;
            }

            if (_state.CurrentTournament.TournamentId != tournamentId)
            {
                errorMessage = "This tournament is no longer active.";
                return false;
            }

            var round = _state.CurrentTournament.Rounds.FirstOrDefault(r => r.RoundNumber == roundNumber);
            if (round is null)
            {
                errorMessage = "Round not found.";
                return false;
            }

            var tournamentMatch = round.Matches.FirstOrDefault(match => match.MatchId == tournamentMatchId);
            if (tournamentMatch is null)
            {
                errorMessage = "Tournament match not found.";
                return false;
            }

            if (tournamentMatch.IsBye)
            {
                errorMessage = "Bye matches cannot be edited.";
                return false;
            }

            if (!tournamentMatch.PlayerBId.HasValue || tournamentMatch.PlayerBId.Value == Guid.Empty)
            {
                errorMessage = "Opponent is missing for this match.";
                return false;
            }

            if (_state.CurrentTournament.MatchingMode == TournamentMatchingMode.Knockout && resultForPlayerA == MatchResult.Draw)
            {
                errorMessage = "Knockout matches must have a winner.";
                return false;
            }

            var playerExists = _state.Players.Any(p => p.Id == tournamentMatch.PlayerAId);
            var opponentExists = _state.Players.Any(p => p.Id == tournamentMatch.PlayerBId.Value);
            if (!playerExists || !opponentExists)
            {
                errorMessage = "Both players must still exist before reporting this result.";
                return false;
            }

            var nowUtc = DateTime.UtcNow;

            if (tournamentMatch.RecordedMatchId.HasValue)
            {
                var matchIndex = _state.Matches.FindIndex(match => match.Id == tournamentMatch.RecordedMatchId.Value);
                if (matchIndex >= 0)
                {
                    _state.Matches[matchIndex] = _state.Matches[matchIndex] with
                    {
                        PlayerId = tournamentMatch.PlayerAId,
                        OpponentId = tournamentMatch.PlayerBId.Value,
                        Result = resultForPlayerA,
                        PlayedOnUtc = nowUtc,
                        SourceTournamentId = _state.CurrentTournament.TournamentId,
                        TournamentRoundNumber = roundNumber
                    };
                }
                else
                {
                    var created = new MatchRecord(
                        Guid.NewGuid(),
                        tournamentMatch.PlayerAId,
                        tournamentMatch.PlayerBId.Value,
                        resultForPlayerA,
                        nowUtc,
                        _state.CurrentTournament.TournamentId,
                        roundNumber);

                    _state.Matches.Add(created);
                    tournamentMatch.RecordedMatchId = created.Id;
                }
            }
            else
            {
                var created = new MatchRecord(
                    Guid.NewGuid(),
                    tournamentMatch.PlayerAId,
                    tournamentMatch.PlayerBId.Value,
                    resultForPlayerA,
                    nowUtc,
                    _state.CurrentTournament.TournamentId,
                    roundNumber);

                _state.Matches.Add(created);
                tournamentMatch.RecordedMatchId = created.Id;
            }

            tournamentMatch.ResultForPlayerA = resultForPlayerA;
            tournamentMatch.ReportedOnUtc = nowUtc;

            ResolveKnockoutBracketLocked(_state.CurrentTournament);
            UpdateTournamentCompletionLocked(_state.CurrentTournament, nowUtc);
        }

        errorMessage = string.Empty;
        return true;
    }

    public bool TryDeleteMostRecentTournamentRound(Guid tournamentId, out int removedMatchCount, out string errorMessage)
    {
        removedMatchCount = 0;

        lock (_sync)
        {
            if (_state.CurrentTournament is null)
            {
                errorMessage = "No active tournament found.";
                return false;
            }

            if (_state.CurrentTournament.TournamentId != tournamentId)
            {
                errorMessage = "This tournament is no longer active.";
                return false;
            }

            if (_state.CurrentTournament.MatchingMode == TournamentMatchingMode.Knockout)
            {
                errorMessage = "Knockout rounds cannot be deleted.";
                return false;
            }

            var latestRound = _state.CurrentTournament.Rounds
                .OrderByDescending(round => round.RoundNumber)
                .FirstOrDefault();

            if (latestRound is null)
            {
                errorMessage = "No round found.";
                return false;
            }

            var recordedMatchIds = latestRound.Matches
                .Where(match => match.RecordedMatchId.HasValue)
                .Select(match => match.RecordedMatchId!.Value)
                .ToHashSet();

            if (recordedMatchIds.Count > 0)
            {
                removedMatchCount = _state.Matches.RemoveAll(match => recordedMatchIds.Contains(match.Id));
            }

            _state.CurrentTournament.Rounds.RemoveAll(round => round.RoundNumber == latestRound.RoundNumber);
            _state.CurrentTournament.Rounds = _state.CurrentTournament.Rounds
                .OrderBy(round => round.RoundNumber)
                .ToList();
            UpdateTournamentCompletionLocked(_state.CurrentTournament, DateTime.UtcNow);
        }

        errorMessage = string.Empty;
        return true;
    }

    public IReadOnlyList<PlayerSummary> GetPlayerSummaries()
    {
        return GetPlayerSummaries(includeInactive: false);
    }

    public IReadOnlyList<PlayerSummary> GetPlayerSummaries(bool includeInactive)
    {
        lock (_sync)
        {
            var players = _state.Players
                .Where(player => includeInactive || player.Active)
                .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(p => p.Name, StringComparer.Ordinal)
                .ToList();

            var summaries = new List<PlayerSummary>(players.Count);

            foreach (var player in players)
            {
                var wins = 0;
                var losses = 0;
                var draws = 0;

                foreach (var match in _state.Matches)
                {
                    if (!match.Result.HasValue)
                    {
                        continue;
                    }

                    if (match.PlayerId == player.Id)
                    {
                        CountResult(match.Result.Value, ref wins, ref losses, ref draws);
                    }
                    else if (match.OpponentId == player.Id)
                    {
                        CountResult(Reverse(match.Result.Value), ref wins, ref losses, ref draws);
                    }
                }

                summaries.Add(new PlayerSummary(player.Id, player.Name, player.Active, wins, losses, draws));
            }

            return summaries;
        }
    }

    public bool TryAddPlayer(string? playerName, out string errorMessage)
    {
        var normalized = (playerName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            errorMessage = "Player name is required.";
            return false;
        }

        lock (_sync)
        {
            var duplicate = _state.Players.Any(player =>
                string.Equals(player.Name, normalized, StringComparison.OrdinalIgnoreCase));

            if (duplicate)
            {
                errorMessage = "A player with this name already exists.";
                return false;
            }

            _state.Players.Add(new Player
            {
                Id = Guid.NewGuid(),
                Name = normalized,
                Active = true
            });
        }

        errorMessage = string.Empty;
        return true;
    }

    public bool RemovePlayer(Guid playerId, out string errorMessage)
    {
        lock (_sync)
        {
            var player = _state.Players.FirstOrDefault(p => p.Id == playerId);
            if (player is null)
            {
                errorMessage = "Player not found.";
                return false;
            }

            _state.Players.Remove(player);
            var removedMatchIds = _state.Matches
                .Where(match => match.PlayerId == playerId || match.OpponentId == playerId)
                .Select(match => match.Id)
                .ToHashSet();

            _state.Matches.RemoveAll(match => removedMatchIds.Contains(match.Id));

            if (removedMatchIds.Count > 0 && _state.CurrentTournament is not null)
            {
                foreach (var tournamentMatch in _state.CurrentTournament.Rounds.SelectMany(round => round.Matches))
                {
                    if (!tournamentMatch.RecordedMatchId.HasValue || !removedMatchIds.Contains(tournamentMatch.RecordedMatchId.Value))
                    {
                        continue;
                    }

                    tournamentMatch.RecordedMatchId = null;
                    if (!tournamentMatch.IsBye)
                    {
                        tournamentMatch.ResultForPlayerA = null;
                        tournamentMatch.ReportedOnUtc = null;
                    }
                }

                UpdateTournamentCompletionLocked(_state.CurrentTournament, DateTime.UtcNow);
            }
        }

        errorMessage = string.Empty;
        return true;
    }

    public bool TryRenamePlayer(Guid playerId, string? newName, out string errorMessage)
    {
        var normalized = (newName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            errorMessage = "Player name is required.";
            return false;
        }

        lock (_sync)
        {
            var playerIndex = _state.Players.FindIndex(player => player.Id == playerId);
            if (playerIndex < 0)
            {
                errorMessage = "Player not found.";
                return false;
            }

            var duplicate = _state.Players.Any(player =>
                player.Id != playerId
                && string.Equals(player.Name, normalized, StringComparison.OrdinalIgnoreCase));

            if (duplicate)
            {
                errorMessage = "A player with this name already exists.";
                return false;
            }

            _state.Players[playerIndex] = _state.Players[playerIndex] with { Name = normalized };
        }

        errorMessage = string.Empty;
        return true;
    }

    public bool TrySetPlayerActive(Guid playerId, bool isActive, out string errorMessage)
    {
        var changed = false;

        lock (_sync)
        {
            var playerIndex = _state.Players.FindIndex(player => player.Id == playerId);
            if (playerIndex < 0)
            {
                errorMessage = "Player not found.";
                return false;
            }

            if (_state.Players[playerIndex].Active != isActive)
            {
                _state.Players[playerIndex] = _state.Players[playerIndex] with { Active = isActive };
                changed = true;
            }
        }

        if (changed)
        {
            OptionsChanged?.Invoke();
        }

        errorMessage = string.Empty;
        return true;
    }

    public bool TryAddMatch(Guid? playerId, Guid? opponentId, MatchResult? result, out string errorMessage)
    {
        return TryAddMatch(playerId, opponentId, result, out _, out errorMessage);
    }

    public bool TryAddMatch(
        Guid? playerId,
        Guid? opponentId,
        MatchResult? result,
        out Guid createdMatchId,
        out string errorMessage)
    {
        createdMatchId = Guid.Empty;

        if (!playerId.HasValue || !opponentId.HasValue)
        {
            errorMessage = "Select both players before adding a match.";
            return false;
        }

        if (!result.HasValue)
        {
            errorMessage = "Select a result before adding a match.";
            return false;
        }

        if (playerId == opponentId)
        {
            errorMessage = "A player cannot play against themselves.";
            return false;
        }

        lock (_sync)
        {
            var playerExists = _state.Players.Any(p => p.Id == playerId.Value);
            var opponentExists = _state.Players.Any(p => p.Id == opponentId.Value);

            if (!playerExists || !opponentExists)
            {
                errorMessage = "Both players must exist before adding a match.";
                return false;
            }

            var match = new MatchRecord(
                Guid.NewGuid(),
                playerId.Value,
                opponentId.Value,
                result.Value,
                DateTime.UtcNow,
                null);

            _state.Matches.Add(match);
            createdMatchId = match.Id;
        }

        errorMessage = string.Empty;
        return true;
    }

    public bool TryUpdateMatch(Guid matchId, Guid? playerId, Guid? opponentId, MatchResult? result, out string errorMessage)
    {
        if (!playerId.HasValue || !opponentId.HasValue)
        {
            errorMessage = "Select both players before updating a match.";
            return false;
        }

        if (!result.HasValue)
        {
            errorMessage = "Select a result before updating a match.";
            return false;
        }

        if (playerId == opponentId)
        {
            errorMessage = "A player cannot play against themselves.";
            return false;
        }

        lock (_sync)
        {
            var matchIndex = _state.Matches.FindIndex(match => match.Id == matchId);
            if (matchIndex < 0)
            {
                errorMessage = "Match not found.";
                return false;
            }

            var playerExists = _state.Players.Any(p => p.Id == playerId.Value);
            var opponentExists = _state.Players.Any(p => p.Id == opponentId.Value);

            if (!playerExists || !opponentExists)
            {
                errorMessage = "Both players must exist before updating a match.";
                return false;
            }

            var nowUtc = DateTime.UtcNow;
            _state.Matches[matchIndex] = _state.Matches[matchIndex] with
            {
                PlayerId = playerId.Value,
                OpponentId = opponentId.Value,
                Result = result.Value,
                PlayedOnUtc = nowUtc
            };

            var currentTournament = _state.CurrentTournament;
            if (currentTournament is not null)
            {
                foreach (var tournamentMatch in currentTournament.Rounds.SelectMany(round => round.Matches))
                {
                    if (tournamentMatch.RecordedMatchId != matchId || tournamentMatch.IsBye || !tournamentMatch.PlayerBId.HasValue)
                    {
                        continue;
                    }

                    if (tournamentMatch.PlayerAId == playerId.Value && tournamentMatch.PlayerBId.Value == opponentId.Value)
                    {
                        tournamentMatch.ResultForPlayerA = result.Value;
                        tournamentMatch.ReportedOnUtc = nowUtc;
                    }
                    else if (tournamentMatch.PlayerAId == opponentId.Value && tournamentMatch.PlayerBId.Value == playerId.Value)
                    {
                        tournamentMatch.ResultForPlayerA = Reverse(result.Value);
                        tournamentMatch.ReportedOnUtc = nowUtc;
                    }
                    else
                    {
                        tournamentMatch.RecordedMatchId = null;
                        tournamentMatch.ResultForPlayerA = null;
                        tournamentMatch.ReportedOnUtc = null;
                    }
                }

                UpdateTournamentCompletionLocked(currentTournament, nowUtc);
            }
        }

        errorMessage = string.Empty;
        return true;
    }

    public bool RemoveMatch(Guid matchId, out string errorMessage)
    {
        lock (_sync)
        {
            var removedCount = _state.Matches.RemoveAll(match => match.Id == matchId);
            if (removedCount == 0)
            {
                errorMessage = "Match not found.";
                return false;
            }

            var currentTournament = _state.CurrentTournament;
            if (currentTournament is not null)
            {
                foreach (var tournamentMatch in currentTournament.Rounds.SelectMany(round => round.Matches))
                {
                    if (tournamentMatch.RecordedMatchId == matchId)
                    {
                        tournamentMatch.RecordedMatchId = null;
                        if (!tournamentMatch.IsBye)
                        {
                            tournamentMatch.ResultForPlayerA = null;
                            tournamentMatch.ReportedOnUtc = null;
                        }
                    }
                }

                UpdateTournamentCompletionLocked(currentTournament, DateTime.UtcNow);
            }
        }

        errorMessage = string.Empty;
        return true;
    }

    public string GetPlayerName(Guid playerId)
    {
        lock (_sync)
        {
            return _state.Players.FirstOrDefault(player => player.Id == playerId)?.Name ?? "Unknown Player";
        }
    }

    public bool IsPlayerActive(Guid playerId)
    {
        lock (_sync)
        {
            return _state.Players.FirstOrDefault(player => player.Id == playerId)?.Active ?? true;
        }
    }

    public static MatchResult Reverse(MatchResult result)
    {
        return result switch
        {
            MatchResult.Win => MatchResult.Loss,
            MatchResult.Loss => MatchResult.Win,
            _ => MatchResult.Draw
        };
    }

    private static void CountResult(MatchResult result, ref int wins, ref int losses, ref int draws)
    {
        switch (result)
        {
            case MatchResult.Win:
                wins++;
                break;
            case MatchResult.Loss:
                losses++;
                break;
            default:
                draws++;
                break;
        }
    }

    private MatchTrackerState CreateSnapshot()
    {
        lock (_sync)
        {
            return new MatchTrackerState
            {
                SchemaVersion = _state.SchemaVersion,
                UsePoints = _state.UsePoints,
                Players = _state.Players.ToList(),
                Matches = _state.Matches.ToList(),
                CurrentTournament = _state.CurrentTournament is null
                    ? null
                    : CloneTournament(_state.CurrentTournament),
                TournamentHistory = _state.TournamentHistory
                    .Select(CloneTournamentHistorySummary)
                    .ToList(),
                LegacyTournamentBracket = null
            };
        }
    }

    private static bool TryDeserializeState(string? serializedState, out MatchTrackerState state)
    {
        if (string.IsNullOrWhiteSpace(serializedState))
        {
            state = new MatchTrackerState();
            return true;
        }

        try
        {
            var parsedState = JsonConvert.DeserializeObject<MatchTrackerState>(serializedState);
            if (parsedState is null)
            {
                state = new MatchTrackerState();
                return false;
            }

            state = SanitizeState(parsedState);
            return true;
        }
        catch (JsonException)
        {
            state = new MatchTrackerState();
            return false;
        }
    }

    private static MatchTrackerState SanitizeState(MatchTrackerState parsedState)
    {
        parsedState.Players ??= new List<Player>();
        parsedState.Matches ??= new List<MatchRecord>();
        parsedState.TournamentHistory ??= new List<TournamentHistorySummary>();
        parsedState.SchemaVersion = Math.Max(parsedState.SchemaVersion, 4);

        var players = parsedState.Players
            .Where(player => player.Id != Guid.Empty)
            .GroupBy(player => player.Id)
            .Select(group =>
            {
                var original = group.First();
                var name = (original.Name ?? string.Empty).Trim();
                return string.IsNullOrWhiteSpace(name)
                    ? null
                    : new Player
                    {
                        Id = original.Id,
                        Name = name,
                        Active = original.Active
                    };
            })
            .Where(player => player is not null)
            .Select(player => player!)
            .ToList();

        var knownPlayerIds = players.Select(player => player.Id).ToHashSet();
        var matches = parsedState.Matches
            .Where(match =>
                match.PlayerId != match.OpponentId
                && knownPlayerIds.Contains(match.PlayerId)
                && knownPlayerIds.Contains(match.OpponentId))
            .Select(match => match.Id == Guid.Empty
                ? new MatchRecord(Guid.NewGuid(), match.PlayerId, match.OpponentId, match.Result, NormalizeUtc(match.PlayedOnUtc), match.SourceTournamentId, match.TournamentRoundNumber)
                : match with
                {
                    PlayedOnUtc = NormalizeUtc(match.PlayedOnUtc),
                    TournamentRoundNumber = Math.Max(match.TournamentRoundNumber, 0)
                })
            .OrderByDescending(match => match.PlayedOnUtc)
            .ToList();

        var tournament = SanitizeTournament(parsedState.CurrentTournament);
        if (tournament is null && parsedState.LegacyTournamentBracket is not null)
        {
            tournament = ConvertLegacyBracket(parsedState.LegacyTournamentBracket);
        }

        if (tournament is not null)
        {
            SyncTournamentMatches(tournament, matches);
            matches = matches
                .OrderByDescending(match => match.PlayedOnUtc)
                .ToList();
        }

        var history = parsedState.TournamentHistory
            .Select(SanitizeTournamentHistorySummary)
            .Where(summary => summary is not null)
            .Select(summary => summary!)
            .OrderByDescending(summary => summary.ArchivedOnUtc)
            .Take(TournamentHistoryLimit)
            .ToList();

        return new MatchTrackerState
        {
            SchemaVersion = parsedState.SchemaVersion,
            UsePoints = parsedState.UsePoints,
            Players = players,
            Matches = matches,
            CurrentTournament = tournament,
            TournamentHistory = history,
            LegacyTournamentBracket = null
        };
    }

    private static TournamentSnapshot CloneTournament(TournamentSnapshot tournament)
    {
        return new TournamentSnapshot
        {
            TournamentId = tournament.TournamentId,
            MatchingMode = tournament.MatchingMode,
            RankMetric = tournament.RankMetric,
            GeneratedOnUtc = tournament.GeneratedOnUtc,
            CompletedOnUtc = tournament.CompletedOnUtc,
            Seeds = tournament.Seeds.Select(seed => seed with { }).ToList(),
            Rounds = tournament.Rounds
                .Select(round => new TournamentRoundSnapshot
                {
                    RoundNumber = round.RoundNumber,
                    Matches = round.Matches
                        .Select(match => new TournamentMatchSnapshot
                        {
                            MatchId = match.MatchId,
                            TableNumber = match.TableNumber,
                            MatchLabel = match.MatchLabel,
                            PlayerASeed = match.PlayerASeed,
                            PlayerAId = match.PlayerAId,
                            PlayerAName = match.PlayerAName,
                            PlayerASourceMatchId = match.PlayerASourceMatchId,
                            PlayerASourceType = match.PlayerASourceType,
                            PlayerBSeed = match.PlayerBSeed,
                            PlayerBId = match.PlayerBId,
                            PlayerBName = match.PlayerBName,
                            PlayerBSourceMatchId = match.PlayerBSourceMatchId,
                            PlayerBSourceType = match.PlayerBSourceType,
                            IsBye = match.IsBye,
                            ResultForPlayerA = match.ResultForPlayerA,
                            ReportedOnUtc = match.ReportedOnUtc,
                            RecordedMatchId = match.RecordedMatchId
                        })
                        .ToList()
                })
                .ToList()
        };
    }

    private static TournamentSnapshot? SanitizeTournament(TournamentSnapshot? parsedTournament)
    {
        if (parsedTournament is null)
        {
            return null;
        }

        parsedTournament.Seeds ??= new List<TournamentSeedSnapshot>();
        parsedTournament.Rounds ??= new List<TournamentRoundSnapshot>();

        var tournamentId = parsedTournament.TournamentId == Guid.Empty
            ? Guid.NewGuid()
            : parsedTournament.TournamentId;

        var matchingMode = Enum.IsDefined(parsedTournament.MatchingMode)
            ? parsedTournament.MatchingMode
            : TournamentMatchingMode.ClassicSwissStage;

        var rankMetric = Enum.IsDefined(parsedTournament.RankMetric)
            ? parsedTournament.RankMetric
            : TournamentRankMetric.WinRate;

        var generatedOnUtc = NormalizeUtc(parsedTournament.GeneratedOnUtc);
        DateTime? completedOnUtc = parsedTournament.CompletedOnUtc.HasValue
            ? NormalizeUtc(parsedTournament.CompletedOnUtc.Value)
            : null;

        var seeds = parsedTournament.Seeds
            .Where(seed => seed.Seed > 0 && seed.PlayerId != Guid.Empty)
            .Select(seed =>
            {
                var name = (seed.Name ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(name))
                {
                    return null;
                }

                var wins = Math.Max(seed.Wins, 0);
                var losses = Math.Max(seed.Losses, 0);
                var draws = Math.Max(seed.Draws, 0);

                return new TournamentSeedSnapshot(
                    seed.Seed,
                    seed.PlayerId,
                    name,
                    wins,
                    losses,
                    draws,
                    seed.Score,
                    BuildWinRateLabel(wins, losses, draws));
            })
            .Where(seed => seed is not null)
            .Select(seed => seed!)
            .OrderBy(seed => seed.Seed)
            .GroupBy(seed => seed.PlayerId)
            .Select(group => group.First())
            .OrderBy(seed => seed.Seed)
            .ToList();

        if (seeds.Count == 0)
        {
            return null;
        }

        var seedOrder = seeds.Select(seed => seed.PlayerId).ToHashSet();

        var rounds = parsedTournament.Rounds
            .Where(round => round.RoundNumber > 0)
            .Select(round =>
            {
                round.Matches ??= new List<TournamentMatchSnapshot>();
                var matches = round.Matches
                    .Where(match => match.TableNumber > 0 &&
                        (!string.IsNullOrWhiteSpace(match.PlayerAName) || match.PlayerASourceMatchId.HasValue))
                    .Select(match => SanitizeTournamentMatch(match, seedOrder))
                    .Where(match => match is not null)
                    .Select(match => match!)
                    .OrderBy(match => match.TableNumber)
                    .ToList();

                return new TournamentRoundSnapshot
                {
                    RoundNumber = round.RoundNumber,
                    Matches = matches
                };
            })
            .Where(round => round.Matches.Count > 0)
            .GroupBy(round => round.RoundNumber)
            .Select(group => group.First())
            .OrderBy(round => round.RoundNumber)
            .ToList();

        if (rounds.Count == 0)
        {
            return null;
        }

        var sanitized = new TournamentSnapshot
        {
            TournamentId = tournamentId,
            MatchingMode = matchingMode,
            RankMetric = rankMetric,
            GeneratedOnUtc = generatedOnUtc,
            CompletedOnUtc = completedOnUtc,
            Seeds = seeds,
            Rounds = rounds
        };

        UpdateTournamentCompletionLocked(sanitized, DateTime.UtcNow);
        return sanitized;
    }

    private static TournamentMatchSnapshot? SanitizeTournamentMatch(
        TournamentMatchSnapshot parsedMatch,
        IReadOnlySet<Guid> seededPlayerIds)
    {
        var playerAName = (parsedMatch.PlayerAName ?? string.Empty).Trim();
        var hasPlayerASource = parsedMatch.PlayerASourceMatchId.HasValue && parsedMatch.PlayerASourceMatchId.Value != Guid.Empty;
        if (string.IsNullOrWhiteSpace(playerAName) && !hasPlayerASource)
        {
            return null;
        }

        var playerAId = parsedMatch.PlayerAId;
        if (playerAId != Guid.Empty && !seededPlayerIds.Contains(playerAId))
        {
            return null;
        }

        var isBye = parsedMatch.IsBye || (!parsedMatch.PlayerBId.HasValue && !parsedMatch.PlayerBSourceMatchId.HasValue);

        Guid? playerBId = null;
        int? playerBSeed = null;
        string? playerBName = null;
        MatchResult? result = parsedMatch.ResultForPlayerA;
        var hasPlayerBSource = parsedMatch.PlayerBSourceMatchId.HasValue && parsedMatch.PlayerBSourceMatchId.Value != Guid.Empty;

        if (!isBye)
        {
            if (parsedMatch.PlayerBId == parsedMatch.PlayerAId && parsedMatch.PlayerBId.HasValue && parsedMatch.PlayerBId.Value != Guid.Empty)
            {
                return null;
            }

            var normalizedPlayerBName = (parsedMatch.PlayerBName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(normalizedPlayerBName) && !hasPlayerBSource)
            {
                return null;
            }

            if (parsedMatch.PlayerBId.HasValue && parsedMatch.PlayerBId.Value != Guid.Empty && !seededPlayerIds.Contains(parsedMatch.PlayerBId.Value))
            {
                return null;
            }

            playerBId = parsedMatch.PlayerBId.HasValue && parsedMatch.PlayerBId.Value != Guid.Empty
                ? parsedMatch.PlayerBId.Value
                : null;
            playerBSeed = parsedMatch.PlayerBSeed.HasValue ? Math.Max(parsedMatch.PlayerBSeed.Value, 0) : null;
            playerBName = string.IsNullOrWhiteSpace(normalizedPlayerBName) ? null : normalizedPlayerBName;

            if (result.HasValue && !Enum.IsDefined(result.Value))
            {
                result = null;
            }
        }
        else
        {
            result = MatchResult.Win;
        }

        if (playerAId == Guid.Empty || (!isBye && playerBId is null))
        {
            result = null;
        }

        return new TournamentMatchSnapshot
        {
            MatchId = parsedMatch.MatchId == Guid.Empty ? Guid.NewGuid() : parsedMatch.MatchId,
            TableNumber = parsedMatch.TableNumber,
            MatchLabel = string.IsNullOrWhiteSpace(parsedMatch.MatchLabel) ? null : parsedMatch.MatchLabel.Trim(),
            PlayerASeed = Math.Max(parsedMatch.PlayerASeed, 0),
            PlayerAId = playerAId,
            PlayerAName = string.IsNullOrWhiteSpace(playerAName)
                ? BuildPendingParticipantLabel(parsedMatch.PlayerASourceType, parsedMatch.PlayerASourceMatchId)
                : playerAName,
            PlayerASourceMatchId = hasPlayerASource ? parsedMatch.PlayerASourceMatchId : null,
            PlayerASourceType = hasPlayerASource ? parsedMatch.PlayerASourceType : null,
            PlayerBSeed = playerBSeed,
            PlayerBId = playerBId,
            PlayerBName = playerBName ?? BuildPendingParticipantLabel(parsedMatch.PlayerBSourceType, parsedMatch.PlayerBSourceMatchId),
            PlayerBSourceMatchId = hasPlayerBSource ? parsedMatch.PlayerBSourceMatchId : null,
            PlayerBSourceType = hasPlayerBSource ? parsedMatch.PlayerBSourceType : null,
            IsBye = isBye,
            ResultForPlayerA = result,
            ReportedOnUtc = parsedMatch.ReportedOnUtc.HasValue
                ? NormalizeUtc(parsedMatch.ReportedOnUtc.Value)
                : null,
            RecordedMatchId = parsedMatch.RecordedMatchId == Guid.Empty
                ? null
                : parsedMatch.RecordedMatchId
        };
    }

    private static TournamentSnapshot? ConvertLegacyBracket(TournamentBracketSnapshot legacyBracket)
    {
        var sanitizedBracket = SanitizeTournamentBracket(legacyBracket);
        if (sanitizedBracket is null)
        {
            return null;
        }

        var roundMatches = BuildClassicSwissRound(sanitizedBracket.Seeds, sanitizedBracket.GeneratedOnUtc);

        return new TournamentSnapshot
        {
            TournamentId = Guid.NewGuid(),
            MatchingMode = sanitizedBracket.MatchingMode,
            RankMetric = sanitizedBracket.RankMetric,
            GeneratedOnUtc = sanitizedBracket.GeneratedOnUtc,
            CompletedOnUtc = null,
            Seeds = sanitizedBracket.Seeds.Select(seed => seed with { }).ToList(),
            Rounds = new List<TournamentRoundSnapshot>
            {
                new()
                {
                    RoundNumber = 1,
                    Matches = roundMatches
                }
            }
        };
    }

    private static List<TournamentMatchSnapshot> BuildClassicSwissRound(
        IReadOnlyList<TournamentSeedSnapshot> seededPlayers,
        DateTime generatedOnUtc)
    {
        var matches = new List<TournamentMatchSnapshot>();
        if (seededPlayers.Count == 0)
        {
            return matches;
        }

        var topHalfSize = (seededPlayers.Count + 1) / 2;
        var tableNumber = 1;

        for (var i = 0; i < topHalfSize; i++)
        {
            var topPlayer = seededPlayers[i];
            var bottomIndex = i + topHalfSize;
            var bottomPlayer = bottomIndex < seededPlayers.Count ? seededPlayers[bottomIndex] : null;

            matches.Add(new TournamentMatchSnapshot
            {
                MatchId = Guid.NewGuid(),
                TableNumber = tableNumber,
                MatchLabel = null,
                PlayerASeed = topPlayer.Seed,
                PlayerAId = topPlayer.PlayerId,
                PlayerAName = topPlayer.Name,
                PlayerASourceMatchId = null,
                PlayerASourceType = null,
                PlayerBSeed = bottomPlayer?.Seed,
                PlayerBId = bottomPlayer?.PlayerId,
                PlayerBName = bottomPlayer?.Name,
                PlayerBSourceMatchId = null,
                PlayerBSourceType = null,
                IsBye = bottomPlayer is null,
                ResultForPlayerA = bottomPlayer is null ? MatchResult.Win : null,
                ReportedOnUtc = bottomPlayer is null ? generatedOnUtc : null,
                RecordedMatchId = null
            });

            tableNumber++;
        }

        return matches;
    }

    private static TournamentHistorySummary CloneTournamentHistorySummary(TournamentHistorySummary summary)
    {
        return new TournamentHistorySummary
        {
            TournamentId = summary.TournamentId,
            MatchingMode = summary.MatchingMode,
            RankMetric = summary.RankMetric,
            GeneratedOnUtc = summary.GeneratedOnUtc,
            ArchivedOnUtc = summary.ArchivedOnUtc,
            CompletedOnUtc = summary.CompletedOnUtc,
            PlayerCount = summary.PlayerCount,
            TotalRounds = summary.TotalRounds,
            TotalMatches = summary.TotalMatches,
            CompletedMatches = summary.CompletedMatches,
            WinnerName = summary.WinnerName,
            Rounds = summary.Rounds
                .Select(round => new TournamentRoundSnapshot
                {
                    RoundNumber = round.RoundNumber,
                    Matches = round.Matches
                        .Select(match => new TournamentMatchSnapshot
                        {
                            MatchId = match.MatchId,
                            TableNumber = match.TableNumber,
                            MatchLabel = match.MatchLabel,
                            PlayerASeed = match.PlayerASeed,
                            PlayerAId = match.PlayerAId,
                            PlayerAName = match.PlayerAName,
                            PlayerASourceMatchId = match.PlayerASourceMatchId,
                            PlayerASourceType = match.PlayerASourceType,
                            PlayerBSeed = match.PlayerBSeed,
                            PlayerBId = match.PlayerBId,
                            PlayerBName = match.PlayerBName,
                            PlayerBSourceMatchId = match.PlayerBSourceMatchId,
                            PlayerBSourceType = match.PlayerBSourceType,
                            IsBye = match.IsBye,
                            ResultForPlayerA = match.ResultForPlayerA,
                            ReportedOnUtc = match.ReportedOnUtc,
                            RecordedMatchId = match.RecordedMatchId
                        })
                        .ToList()
                })
                .ToList()
        };
    }

    private static TournamentHistorySummary? SanitizeTournamentHistorySummary(TournamentHistorySummary? parsedSummary)
    {
        if (parsedSummary is null)
        {
            return null;
        }

        if (parsedSummary.PlayerCount < 0 || parsedSummary.TotalRounds < 0 || parsedSummary.TotalMatches < 0 || parsedSummary.CompletedMatches < 0)
        {
            return null;
        }

        var tournamentId = parsedSummary.TournamentId == Guid.Empty
            ? Guid.NewGuid()
            : parsedSummary.TournamentId;

        var matchingMode = Enum.IsDefined(parsedSummary.MatchingMode)
            ? parsedSummary.MatchingMode
            : TournamentMatchingMode.ClassicSwissStage;

        var rankMetric = Enum.IsDefined(parsedSummary.RankMetric)
            ? parsedSummary.RankMetric
            : TournamentRankMetric.WinRate;

        var winnerName = (parsedSummary.WinnerName ?? string.Empty).Trim();
        parsedSummary.Rounds ??= new List<TournamentRoundSnapshot>();
        var roundSeedOrder = parsedSummary.Rounds
            .SelectMany(round => round.Matches)
            .Select(match => match.PlayerAId)
            .Where(id => id != Guid.Empty)
            .Concat(parsedSummary.Rounds
                .SelectMany(round => round.Matches)
                .Where(match => match.PlayerBId.HasValue)
                .Select(match => match.PlayerBId!.Value))
            .ToHashSet();
        var rounds = parsedSummary.Rounds
            .Where(round => round.RoundNumber > 0)
            .Select(round =>
            {
                round.Matches ??= new List<TournamentMatchSnapshot>();
                return new TournamentRoundSnapshot
                {
                    RoundNumber = round.RoundNumber,
                    Matches = round.Matches
                        .Where(match => match.TableNumber > 0 && match.PlayerAId != Guid.Empty && !string.IsNullOrWhiteSpace(match.PlayerAName))
                        .Select(match => SanitizeTournamentMatch(match, roundSeedOrder))
                        .Where(match => match is not null)
                        .Select(match => match!)
                        .OrderBy(match => match.TableNumber)
                        .ToList()
                };
            })
            .Where(round => round.Matches.Count > 0)
            .OrderBy(round => round.RoundNumber)
            .ToList();

        return new TournamentHistorySummary
        {
            TournamentId = tournamentId,
            MatchingMode = matchingMode,
            RankMetric = rankMetric,
            GeneratedOnUtc = NormalizeUtc(parsedSummary.GeneratedOnUtc),
            ArchivedOnUtc = NormalizeUtc(parsedSummary.ArchivedOnUtc),
            CompletedOnUtc = parsedSummary.CompletedOnUtc.HasValue
                ? NormalizeUtc(parsedSummary.CompletedOnUtc.Value)
                : null,
            PlayerCount = parsedSummary.PlayerCount,
            TotalRounds = parsedSummary.TotalRounds,
            TotalMatches = parsedSummary.TotalMatches,
            CompletedMatches = Math.Min(parsedSummary.CompletedMatches, parsedSummary.TotalMatches),
            WinnerName = string.IsNullOrWhiteSpace(winnerName) ? null : winnerName,
            Rounds = rounds
        };
    }

    private static TournamentBracketSnapshot? SanitizeTournamentBracket(TournamentBracketSnapshot? parsedBracket)
    {
        if (parsedBracket is null)
        {
            return null;
        }

        parsedBracket.Seeds ??= new List<TournamentSeedSnapshot>();

        var matchingMode = Enum.IsDefined(parsedBracket.MatchingMode)
            ? parsedBracket.MatchingMode
            : TournamentMatchingMode.ClassicSwissStage;

        var rankMetric = Enum.IsDefined(parsedBracket.RankMetric)
            ? parsedBracket.RankMetric
            : TournamentRankMetric.WinRate;

        var generatedOnUtc = parsedBracket.GeneratedOnUtc == default
            ? DateTime.UtcNow
            : NormalizeUtc(parsedBracket.GeneratedOnUtc);

        var seeds = parsedBracket.Seeds
            .Where(seed => seed.Seed > 0 && seed.PlayerId != Guid.Empty)
            .Select(seed =>
            {
                var name = (seed.Name ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(name))
                {
                    return null;
                }

                var wins = Math.Max(seed.Wins, 0);
                var losses = Math.Max(seed.Losses, 0);
                var draws = Math.Max(seed.Draws, 0);

                return new TournamentSeedSnapshot(
                    seed.Seed,
                    seed.PlayerId,
                    name,
                    wins,
                    losses,
                    draws,
                    seed.Score,
                    BuildWinRateLabel(wins, losses, draws));
            })
            .Where(seed => seed is not null)
            .Select(seed => seed!)
            .OrderBy(seed => seed.Seed)
            .GroupBy(seed => seed.Seed)
            .Select(group => group.First())
            .ToList();

        if (seeds.Count == 0)
        {
            return null;
        }

        return new TournamentBracketSnapshot
        {
            MatchingMode = matchingMode,
            RankMetric = rankMetric,
            GeneratedOnUtc = generatedOnUtc,
            Seeds = seeds
        };
    }

    private void ResolveKnockoutBracketLocked(TournamentSnapshot tournamentSnapshot)
    {
        if (tournamentSnapshot.MatchingMode != TournamentMatchingMode.Knockout)
        {
            return;
        }

        var matchesById = tournamentSnapshot.Rounds
            .SelectMany(round => round.Matches)
            .ToDictionary(match => match.MatchId);

        foreach (var round in tournamentSnapshot.Rounds.OrderBy(round => round.RoundNumber))
        {
            foreach (var match in round.Matches.OrderBy(match => match.TableNumber))
            {
                ResolveParticipantSlotLocked(match, isPlayerA: true, matchesById);
                ResolveParticipantSlotLocked(match, isPlayerA: false, matchesById);

                match.IsBye = match.PlayerAId != Guid.Empty && !match.PlayerBId.HasValue && !match.PlayerBSourceMatchId.HasValue;
                if (match.IsBye)
                {
                    if (match.RecordedMatchId.HasValue)
                    {
                        _state.Matches.RemoveAll(existing => existing.Id == match.RecordedMatchId.Value);
                        match.RecordedMatchId = null;
                    }

                    match.ResultForPlayerA = MatchResult.Win;
                    match.ReportedOnUtc ??= tournamentSnapshot.GeneratedOnUtc;
                }
                else if (match.PlayerAId == Guid.Empty || !match.PlayerBId.HasValue || match.PlayerBId.Value == Guid.Empty)
                {
                    ClearDependentRecordedMatchLocked(match);
                }
            }
        }
    }

    private void ResolveParticipantSlotLocked(
        TournamentMatchSnapshot match,
        bool isPlayerA,
        IReadOnlyDictionary<Guid, TournamentMatchSnapshot> matchesById)
    {
        var sourceMatchId = isPlayerA ? match.PlayerASourceMatchId : match.PlayerBSourceMatchId;
        var sourceType = isPlayerA ? match.PlayerASourceType : match.PlayerBSourceType;
        if (!sourceMatchId.HasValue || !sourceType.HasValue || !matchesById.TryGetValue(sourceMatchId.Value, out var sourceMatch))
        {
            return;
        }

        var participant = TryResolveDependentParticipant(sourceMatch, sourceType.Value);
        if (participant is null)
        {
            SetParticipant(match, isPlayerA, 0, Guid.Empty, BuildPendingParticipantLabel(sourceType, sourceMatchId));
            ClearDependentRecordedMatchLocked(match);
            return;
        }

        var changed = isPlayerA
            ? match.PlayerAId != participant.PlayerId || match.PlayerASeed != participant.Seed || !string.Equals(match.PlayerAName, participant.Name, StringComparison.Ordinal)
            : match.PlayerBId != participant.PlayerId || match.PlayerBSeed != participant.Seed || !string.Equals(match.PlayerBName, participant.Name, StringComparison.Ordinal);

        SetParticipant(match, isPlayerA, participant.Seed, participant.PlayerId, participant.Name);

        if (changed)
        {
            ClearDependentRecordedMatchLocked(match);
        }
    }

    private void ClearDependentRecordedMatchLocked(TournamentMatchSnapshot match)
    {
        if (match.RecordedMatchId.HasValue)
        {
            _state.Matches.RemoveAll(existing => existing.Id == match.RecordedMatchId.Value);
        }

        match.RecordedMatchId = null;
        if (!match.IsBye)
        {
            match.ResultForPlayerA = null;
            match.ReportedOnUtc = null;
        }
    }

    private static void SetParticipant(TournamentMatchSnapshot match, bool isPlayerA, int seed, Guid playerId, string name)
    {
        if (isPlayerA)
        {
            match.PlayerASeed = seed;
            match.PlayerAId = playerId;
            match.PlayerAName = name;
        }
        else
        {
            match.PlayerBSeed = seed <= 0 ? null : seed;
            match.PlayerBId = playerId == Guid.Empty ? null : playerId;
            match.PlayerBName = name;
        }
    }

    private static ResolvedTournamentParticipant? TryResolveDependentParticipant(
        TournamentMatchSnapshot sourceMatch,
        TournamentMatchParticipantSourceType sourceType)
    {
        if (sourceType == TournamentMatchParticipantSourceType.Winner)
        {
            if (sourceMatch.IsBye)
            {
                return sourceMatch.PlayerAId == Guid.Empty
                    ? null
                    : new ResolvedTournamentParticipant(sourceMatch.PlayerASeed, sourceMatch.PlayerAId, sourceMatch.PlayerAName);
            }

            return sourceMatch.ResultForPlayerA switch
            {
                MatchResult.Win when sourceMatch.PlayerAId != Guid.Empty => new ResolvedTournamentParticipant(sourceMatch.PlayerASeed, sourceMatch.PlayerAId, sourceMatch.PlayerAName),
                MatchResult.Loss when sourceMatch.PlayerBId.HasValue && sourceMatch.PlayerBId.Value != Guid.Empty =>
                    new ResolvedTournamentParticipant(sourceMatch.PlayerBSeed.GetValueOrDefault(), sourceMatch.PlayerBId.Value, sourceMatch.PlayerBName ?? string.Empty),
                _ => null
            };
        }

        if (sourceMatch.IsBye)
        {
            return null;
        }

        return sourceMatch.ResultForPlayerA switch
        {
            MatchResult.Loss when sourceMatch.PlayerAId != Guid.Empty => new ResolvedTournamentParticipant(sourceMatch.PlayerASeed, sourceMatch.PlayerAId, sourceMatch.PlayerAName),
            MatchResult.Win when sourceMatch.PlayerBId.HasValue && sourceMatch.PlayerBId.Value != Guid.Empty =>
                new ResolvedTournamentParticipant(sourceMatch.PlayerBSeed.GetValueOrDefault(), sourceMatch.PlayerBId.Value, sourceMatch.PlayerBName ?? string.Empty),
            _ => null
        };
    }

    private static string BuildPendingParticipantLabel(
        TournamentMatchParticipantSourceType? sourceType,
        Guid? sourceMatchId)
    {
        if (!sourceType.HasValue || !sourceMatchId.HasValue || sourceMatchId.Value == Guid.Empty)
        {
            return "TBD";
        }

        return sourceType.Value == TournamentMatchParticipantSourceType.Winner
            ? "Winner TBD"
            : "Loser TBD";
    }

    private void SyncTournamentMatchesLocked(TournamentSnapshot tournamentSnapshot)
    {
        SyncTournamentMatches(tournamentSnapshot, _state.Matches);
    }

    private static void SyncTournamentMatches(TournamentSnapshot tournamentSnapshot, List<MatchRecord> matches)
    {
        foreach (var tournamentMatch in tournamentSnapshot.Rounds.SelectMany(round => round.Matches))
        {
            if (tournamentMatch.IsBye
                || tournamentMatch.PlayerAId == Guid.Empty
                || !tournamentMatch.PlayerBId.HasValue
                || tournamentMatch.PlayerBId.Value == Guid.Empty)
            {
                continue;
            }

            var matchId = tournamentMatch.RecordedMatchId.GetValueOrDefault();
            var playedOnUtc = tournamentMatch.ReportedOnUtc ?? tournamentSnapshot.GeneratedOnUtc;

            if (matchId != Guid.Empty)
            {
                var existingIndex = matches.FindIndex(match => match.Id == matchId);
                if (existingIndex >= 0)
                {
                    matches[existingIndex] = matches[existingIndex] with
                    {
                        PlayerId = tournamentMatch.PlayerAId,
                        OpponentId = tournamentMatch.PlayerBId.Value,
                        Result = tournamentMatch.ResultForPlayerA,
                        PlayedOnUtc = playedOnUtc,
                        SourceTournamentId = tournamentSnapshot.TournamentId,
                        TournamentRoundNumber = tournamentSnapshot.Rounds
                            .FirstOrDefault(round => round.Matches.Contains(tournamentMatch))?.RoundNumber ?? 0
                    };
                    continue;
                }
            }

            var createdId = matchId == Guid.Empty ? Guid.NewGuid() : matchId;
            matches.Add(new MatchRecord(
                createdId,
                tournamentMatch.PlayerAId,
                tournamentMatch.PlayerBId.Value,
                tournamentMatch.ResultForPlayerA,
                playedOnUtc,
                tournamentSnapshot.TournamentId,
                tournamentSnapshot.Rounds.FirstOrDefault(round => round.Matches.Contains(tournamentMatch))?.RoundNumber ?? 0));

            tournamentMatch.RecordedMatchId = createdId;
        }
    }

    private void ArchiveTournamentLocked(TournamentSnapshot tournamentSnapshot, DateTime archivedOnUtc)
    {
        var summary = BuildTournamentHistorySummary(tournamentSnapshot, archivedOnUtc);
        _state.TournamentHistory.Insert(0, summary);

        if (_state.TournamentHistory.Count > TournamentHistoryLimit)
        {
            _state.TournamentHistory = _state.TournamentHistory
                .Take(TournamentHistoryLimit)
                .ToList();
        }
    }

    private static TournamentHistorySummary BuildTournamentHistorySummary(TournamentSnapshot tournamentSnapshot, DateTime archivedOnUtc)
    {
        var archivedRounds = TrimTrailingUnstartedRounds(tournamentSnapshot.Rounds);
        var archivedTournament = new TournamentSnapshot
        {
            TournamentId = tournamentSnapshot.TournamentId,
            MatchingMode = tournamentSnapshot.MatchingMode,
            RankMetric = tournamentSnapshot.RankMetric,
            GeneratedOnUtc = tournamentSnapshot.GeneratedOnUtc,
            CompletedOnUtc = tournamentSnapshot.CompletedOnUtc,
            Seeds = tournamentSnapshot.Seeds.Select(seed => seed with { }).ToList(),
            Rounds = archivedRounds
        };

        var standings = BuildTournamentStandings(archivedTournament).ToList();
        var totalMatches = archivedTournament.Rounds
            .SelectMany(round => round.Matches)
            .Count(match => !match.IsBye);

        var completedMatches = archivedTournament.Rounds
            .SelectMany(round => round.Matches)
            .Count(match => !match.IsBye && match.ResultForPlayerA.HasValue);

        var winner = standings
            .OrderByDescending(row => row.Points)
            .ThenByDescending(row => row.Wins)
            .ThenBy(row => row.Seed)
            .FirstOrDefault();

        return new TournamentHistorySummary
        {
            TournamentId = tournamentSnapshot.TournamentId,
            MatchingMode = tournamentSnapshot.MatchingMode,
            RankMetric = tournamentSnapshot.RankMetric,
            GeneratedOnUtc = tournamentSnapshot.GeneratedOnUtc,
            ArchivedOnUtc = NormalizeUtc(archivedOnUtc),
            CompletedOnUtc = tournamentSnapshot.CompletedOnUtc,
            PlayerCount = tournamentSnapshot.Seeds.Count,
            TotalRounds = archivedTournament.Rounds.Count,
            TotalMatches = totalMatches,
            CompletedMatches = completedMatches,
            WinnerName = winner?.Name,
            Rounds = archivedTournament.Rounds
                .OrderBy(round => round.RoundNumber)
                .Select(round => new TournamentRoundSnapshot
                {
                    RoundNumber = round.RoundNumber,
                    Matches = round.Matches
                        .OrderBy(match => match.TableNumber)
                        .Select(match => new TournamentMatchSnapshot
                        {
                            MatchId = match.MatchId,
                            TableNumber = match.TableNumber,
                            MatchLabel = match.MatchLabel,
                            PlayerASeed = match.PlayerASeed,
                            PlayerAId = match.PlayerAId,
                            PlayerAName = match.PlayerAName,
                            PlayerASourceMatchId = match.PlayerASourceMatchId,
                            PlayerASourceType = match.PlayerASourceType,
                            PlayerBSeed = match.PlayerBSeed,
                            PlayerBId = match.PlayerBId,
                            PlayerBName = match.PlayerBName,
                            PlayerBSourceMatchId = match.PlayerBSourceMatchId,
                            PlayerBSourceType = match.PlayerBSourceType,
                            IsBye = match.IsBye,
                            ResultForPlayerA = match.ResultForPlayerA,
                            ReportedOnUtc = match.ReportedOnUtc,
                            RecordedMatchId = match.RecordedMatchId
                        })
                        .ToList()
                })
                .ToList()
        };
    }

    private static List<TournamentRoundSnapshot> TrimTrailingUnstartedRounds(IReadOnlyList<TournamentRoundSnapshot> rounds)
    {
        var trimmed = rounds
            .OrderBy(round => round.RoundNumber)
            .Select(round => new TournamentRoundSnapshot
            {
                RoundNumber = round.RoundNumber,
                Matches = round.Matches
                    .OrderBy(match => match.TableNumber)
                    .Select(match => new TournamentMatchSnapshot
                    {
                        MatchId = match.MatchId,
                        TableNumber = match.TableNumber,
                        MatchLabel = match.MatchLabel,
                        PlayerASeed = match.PlayerASeed,
                        PlayerAId = match.PlayerAId,
                        PlayerAName = match.PlayerAName,
                        PlayerASourceMatchId = match.PlayerASourceMatchId,
                        PlayerASourceType = match.PlayerASourceType,
                        PlayerBSeed = match.PlayerBSeed,
                        PlayerBId = match.PlayerBId,
                        PlayerBName = match.PlayerBName,
                        PlayerBSourceMatchId = match.PlayerBSourceMatchId,
                        PlayerBSourceType = match.PlayerBSourceType,
                        IsBye = match.IsBye,
                        ResultForPlayerA = match.ResultForPlayerA,
                        ReportedOnUtc = match.ReportedOnUtc,
                        RecordedMatchId = match.RecordedMatchId
                    })
                    .ToList()
            })
            .ToList();

        while (trimmed.Count > 0 && IsRoundUnstarted(trimmed[^1]))
        {
            trimmed.RemoveAt(trimmed.Count - 1);
        }

        return trimmed;
    }

    private static bool IsRoundUnstarted(TournamentRoundSnapshot round)
    {
        return round.Matches
            .Where(match => !match.IsBye)
            .All(match => !match.ResultForPlayerA.HasValue);
    }

    private static IEnumerable<TournamentStandingRow> BuildTournamentStandings(TournamentSnapshot tournamentSnapshot)
    {
        var rows = tournamentSnapshot.Seeds
            .Select(seed => new TournamentStandingRow(seed.Seed, seed.PlayerId, seed.Name))
            .ToDictionary(row => row.PlayerId, row => row);

        foreach (var match in tournamentSnapshot.Rounds
                     .OrderBy(round => round.RoundNumber)
                     .SelectMany(round => round.Matches.OrderBy(match => match.TableNumber)))
        {
            if (!rows.TryGetValue(match.PlayerAId, out var rowA))
            {
                continue;
            }

            if (match.IsBye)
            {
                rowA.Wins++;
                rowA.Points += 3;
                continue;
            }

            if (!match.PlayerBId.HasValue || !rows.TryGetValue(match.PlayerBId.Value, out var rowB) || !match.ResultForPlayerA.HasValue)
            {
                continue;
            }

            ApplyStandingResult(rowA, rowB, match.ResultForPlayerA.Value);
        }

        return rows.Values;
    }

    private static void ApplyStandingResult(TournamentStandingRow rowA, TournamentStandingRow rowB, MatchResult resultForPlayerA)
    {
        switch (resultForPlayerA)
        {
            case MatchResult.Win:
                rowA.Wins++;
                rowA.Points += 3;
                rowB.Losses++;
                break;
            case MatchResult.Loss:
                rowA.Losses++;
                rowB.Wins++;
                rowB.Points += 3;
                break;
            default:
                rowA.Draws++;
                rowB.Draws++;
                rowA.Points++;
                rowB.Points++;
                break;
        }
    }

    private static void UpdateTournamentCompletionLocked(TournamentSnapshot tournamentSnapshot, DateTime nowUtc)
    {
        var allPlayableMatches = tournamentSnapshot.Rounds
            .SelectMany(round => round.Matches)
            .Where(match => !match.IsBye)
            .ToList();

        if (allPlayableMatches.Count == 0 || allPlayableMatches.All(match => match.ResultForPlayerA.HasValue))
        {
            tournamentSnapshot.CompletedOnUtc ??= NormalizeUtc(nowUtc);
        }
        else
        {
            tournamentSnapshot.CompletedOnUtc = null;
        }
    }

    private static DateTime NormalizeUtc(DateTime timestamp)
    {
        if (timestamp == default)
        {
            return DateTime.UtcNow;
        }

        return timestamp.Kind == DateTimeKind.Utc
            ? timestamp
            : timestamp.ToUniversalTime();
    }

    private static string BuildWinRateLabel(int wins, int losses, int draws)
    {
        var matchesPlayed = wins + losses + draws;
        if (matchesPlayed == 0)
        {
            return "0% WR";
        }

        var winRatePercent = (int)Math.Round((double)wins / matchesPlayed * 100, MidpointRounding.AwayFromZero);
        return $"{winRatePercent}% WR";
    }

    private sealed class TournamentStandingRow
    {
        public TournamentStandingRow(int seed, Guid playerId, string name)
        {
            Seed = seed;
            PlayerId = playerId;
            Name = name;
        }

        public int Seed { get; }
        public Guid PlayerId { get; }
        public string Name { get; }
        public int Wins { get; set; }
        public int Losses { get; set; }
        public int Draws { get; set; }
        public int Points { get; set; }
    }
}

public sealed class MatchTrackerState
{
    public int SchemaVersion { get; set; } = 4;
    public bool UsePoints { get; set; } = true;
    public List<Player> Players { get; set; } = new();
    public List<MatchRecord> Matches { get; set; } = new();
    public TournamentSnapshot? CurrentTournament { get; set; }
    public List<TournamentHistorySummary> TournamentHistory { get; set; } = new();

    [JsonProperty("tournamentBracket")]
    public TournamentBracketSnapshot? LegacyTournamentBracket { get; set; }
}

public sealed record Player
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
    [DefaultValue(true)]
    public bool Active { get; init; } = true;
}

public sealed record MatchRecord(
    Guid Id,
    Guid PlayerId,
    Guid OpponentId,
    MatchResult? Result,
    DateTime PlayedOnUtc,
    Guid? SourceTournamentId,
    int TournamentRoundNumber = 0);

public sealed record PlayerSummary(
    Guid PlayerId,
    string Name,
    bool Active,
    int Wins,
    int Losses,
    int Draws)
{
    public const int StartingScore = 100;
    public const int WinPoints = 20;
    public const int DrawPenalty = 5;
    public const int LossPenalty = 15;

    public int MatchesPlayed => Wins + Losses + Draws;
    public int Score => StartingScore + (Wins * WinPoints) - (Draws * DrawPenalty) - (Losses * LossPenalty);
    public double WinRate => MatchesPlayed == 0 ? 0d : (double)Wins / MatchesPlayed;
    public int WinRatePercent => (int)Math.Round(WinRate * 100, MidpointRounding.AwayFromZero);
    public string WinRateLabel => $"{WinRatePercent}% WR";
}

public enum MatchResult
{
    Win,
    Loss,
    Draw
}

public enum TournamentMatchingMode
{
    ClassicSwissStage,
    MonradSwiss,
    RoundRobin,
    Knockout
}

public enum TournamentMatchParticipantSourceType
{
    Winner,
    Loser
}

public enum TournamentRankMetric
{
    WinRate,
    TotalWins,
    Points,
    Random
}

public sealed class TournamentSnapshot
{
    public Guid TournamentId { get; set; } = Guid.NewGuid();
    public TournamentMatchingMode MatchingMode { get; set; } = TournamentMatchingMode.ClassicSwissStage;
    public TournamentRankMetric RankMetric { get; set; } = TournamentRankMetric.WinRate;
    public DateTime GeneratedOnUtc { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedOnUtc { get; set; }
    public List<TournamentSeedSnapshot> Seeds { get; set; } = new();
    public List<TournamentRoundSnapshot> Rounds { get; set; } = new();
}

public sealed class TournamentRoundSnapshot
{
    public int RoundNumber { get; set; }
    public List<TournamentMatchSnapshot> Matches { get; set; } = new();
}

public sealed class TournamentMatchSnapshot
{
    public Guid MatchId { get; set; } = Guid.NewGuid();
    public int TableNumber { get; set; }
    public string? MatchLabel { get; set; }
    public int PlayerASeed { get; set; }
    public Guid PlayerAId { get; set; }
    public string PlayerAName { get; set; } = string.Empty;
    public Guid? PlayerASourceMatchId { get; set; }
    public TournamentMatchParticipantSourceType? PlayerASourceType { get; set; }
    public int? PlayerBSeed { get; set; }
    public Guid? PlayerBId { get; set; }
    public string? PlayerBName { get; set; }
    public Guid? PlayerBSourceMatchId { get; set; }
    public TournamentMatchParticipantSourceType? PlayerBSourceType { get; set; }
    public bool IsBye { get; set; }
    public MatchResult? ResultForPlayerA { get; set; }
    public DateTime? ReportedOnUtc { get; set; }
    public Guid? RecordedMatchId { get; set; }
}

public sealed class TournamentHistorySummary
{
    public Guid TournamentId { get; set; }
    public TournamentMatchingMode MatchingMode { get; set; }
    public TournamentRankMetric RankMetric { get; set; }
    public DateTime GeneratedOnUtc { get; set; }
    public DateTime ArchivedOnUtc { get; set; }
    public DateTime? CompletedOnUtc { get; set; }
    public int PlayerCount { get; set; }
    public int TotalRounds { get; set; }
    public int TotalMatches { get; set; }
    public int CompletedMatches { get; set; }
    public string? WinnerName { get; set; }
    public List<TournamentRoundSnapshot> Rounds { get; set; } = new();
}

public sealed class TournamentBracketSnapshot
{
    public TournamentMatchingMode MatchingMode { get; set; } = TournamentMatchingMode.ClassicSwissStage;
    public TournamentRankMetric RankMetric { get; set; } = TournamentRankMetric.WinRate;
    public DateTime GeneratedOnUtc { get; set; } = DateTime.UtcNow;
    public List<TournamentSeedSnapshot> Seeds { get; set; } = new();
}

public sealed record TournamentSeedSnapshot(
    int Seed,
    Guid PlayerId,
    string Name,
    int Wins,
    int Losses,
    int Draws,
    int Score,
    string WinRateLabel);

sealed record ResolvedTournamentParticipant(int Seed, Guid PlayerId, string Name);
