using AlbionCompanion.Gathering;
using AlbionCompanion.Gathering.LiveEvents;
using Microsoft.Extensions.DependencyInjection;

namespace AlbionCompanion.App;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(new MainPage()) { Title = "AlbionCompanion" };
        var startupTask = ConnectAsync();

        window.Destroying += async (_, _) =>
        {
            await startupTask;
            MauiProgram.GatheringProvider?.GetRequiredService<LiveEventPipeClient>().Dispose();
            MauiProgram.GatheringSessionScope?.Dispose();
        };

        return window;
    }

    private static async Task ConnectAsync()
    {
        if (MauiProgram.GatheringProvider is null)
        {
            return;
        }

        try
        {
            var sessionScope = MauiProgram.GatheringProvider.CreateScope();
            MauiProgram.GatheringSessionScope = sessionScope;
            var sessionService = sessionScope.ServiceProvider.GetRequiredService<IGatheringSessionService>();
            var pipeClient = MauiProgram.GatheringProvider.GetRequiredService<LiveEventPipeClient>();

            if (MauiProgram.Services?.GetRequiredService<IGatheringLiveState>() is { } liveState)
            {
                await liveState.Attach(sessionService, pipeClient);
            }

            _ = pipeClient.StartAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            if (MauiProgram.ProgramDataPath is not null)
            {
                var logPath = Path.Combine(MauiProgram.ProgramDataPath, "debug_maui_startup_failures.log");
                await File.AppendAllTextAsync(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {ex.GetType().Name}: {ex.Message}{Environment.NewLine}");
            }
        }
    }
}
