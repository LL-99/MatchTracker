using MatchTracker.Components;
using MatchTracker.Services;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using MudBlazor.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");
builder.Services.AddMudServices(options =>
{
    options.SnackbarConfiguration.PositionClass = MudBlazor.Defaults.Classes.Position.BottomRight;
});
builder.Services.AddScoped<MatchTrackerService>();

await builder.Build().RunAsync();
