using Microsoft.JSInterop;
using Newtonsoft.Json;

namespace MatchTracker.Services;

public sealed class MatchTrackerService
{
    private const string LocalStorageKey = "matchtracker.state.v1";
    private readonly object _sync = new();
    private MatchTrackerState _state = new();
    private bool _isInitialized;

    public async Task EnsureInitializedAsync(IJSRuntime jsRuntime)
    {
        lock (_sync)
        {
            if (_isInitialized)
            {
                return;
            }
        }

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

        errorMessage = string.Empty;
        return true;
    }

    public IReadOnlyList<Player> GetPlayers()
    {
        lock (_sync)
        {
            return _state.Players
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

    public IReadOnlyList<PlayerSummary> GetPlayerSummaries()
    {
        lock (_sync)
        {
            var summaries = new List<PlayerSummary>(_state.Players.Count);

            foreach (var player in _state.Players
                .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(p => p.Name, StringComparer.Ordinal))
            {
                var wins = 0;
                var losses = 0;
                var draws = 0;

                foreach (var match in _state.Matches)
                {
                    if (match.PlayerId == player.Id)
                    {
                        CountResult(match.Result, ref wins, ref losses, ref draws);
                    }
                    else if (match.OpponentId == player.Id)
                    {
                        CountResult(Reverse(match.Result), ref wins, ref losses, ref draws);
                    }
                }

                summaries.Add(new PlayerSummary(player.Id, player.Name, wins, losses, draws));
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

            _state.Players.Add(new Player(Guid.NewGuid(), normalized));
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
            _state.Matches.RemoveAll(match => match.PlayerId == playerId || match.OpponentId == playerId);
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

    public bool TryAddMatch(Guid? playerId, Guid? opponentId, MatchResult? result, out string errorMessage)
    {
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

            _state.Matches.Add(new MatchRecord(
                Guid.NewGuid(),
                playerId.Value,
                opponentId.Value,
                result.Value,
                DateTime.UtcNow));
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

            _state.Matches[matchIndex] = _state.Matches[matchIndex] with
            {
                PlayerId = playerId.Value,
                OpponentId = opponentId.Value,
                Result = result.Value
            };
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
                Players = _state.Players.ToList(),
                Matches = _state.Matches.ToList()
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
        parsedState.SchemaVersion = Math.Max(parsedState.SchemaVersion, 1);

        var players = parsedState.Players
            .Where(player => player.Id != Guid.Empty)
            .GroupBy(player => player.Id)
            .Select(group =>
            {
                var original = group.First();
                var name = (original.Name ?? string.Empty).Trim();
                return string.IsNullOrWhiteSpace(name) ? null : new Player(original.Id, name);
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
                ? new MatchRecord(Guid.NewGuid(), match.PlayerId, match.OpponentId, match.Result, match.PlayedOnUtc)
                : match)
            .OrderByDescending(match => match.PlayedOnUtc)
            .ToList();

        return new MatchTrackerState
        {
            SchemaVersion = parsedState.SchemaVersion,
            Players = players,
            Matches = matches
        };
    }
}

public sealed class MatchTrackerState
{
    public int SchemaVersion { get; set; } = 1;
    public List<Player> Players { get; set; } = new();
    public List<MatchRecord> Matches { get; set; } = new();
}

public sealed record Player(Guid Id, string Name);

public sealed record MatchRecord(
    Guid Id,
    Guid PlayerId,
    Guid OpponentId,
    MatchResult Result,
    DateTime PlayedOnUtc);

public sealed record PlayerSummary(
    Guid PlayerId,
    string Name,
    int Wins,
    int Losses,
    int Draws)
{
    public int MatchesPlayed => Wins + Losses + Draws;
}

public enum MatchResult
{
    Win,
    Loss,
    Draw
}
