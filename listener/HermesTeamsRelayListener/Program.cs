using HermesTeamsRelayListener;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// Generic Host, not a raw Main loop: this is what lets the exact same binary
// run either as a normal console app (`dotnet run`, for a quick manual test)
// or as an installed Windows Service (`sc create ... binPath= "...exe"`),
// with the host correctly translating both Ctrl+C and Windows Service Control
// Manager stop requests into the same graceful-shutdown path.
IHost host = Host.CreateDefaultBuilder(args)
    .UseWindowsService(options =>
    {
        options.ServiceName = "HermesTeamsRelayListener";
    })
    .ConfigureServices(services =>
    {
        services.AddHostedService<RelayListenerWorker>();
    })
    .Build();

await host.RunAsync();
