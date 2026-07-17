using AlbionCompanion.Gathering;
using AlbionCompanion.Sniffer.PacketCapture;
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
        var startupTask = StartGatheringAsync();

        window.Destroying += async (_, _) =>
        {
            await startupTask;
            MauiProgram.GatheringProvider?.GetRequiredService<IPacketSniffer>().Stop();
            MauiProgram.GatheringSessionScope?.Dispose();
        };

        return window;
    }

    private static async Task StartGatheringAsync()
    {
        if (MauiProgram.GatheringProvider is null)
        {
            return;
        }

        try
        {
            var sessionScope = await AppHostBuilder.RunStartupSequenceAsync(MauiProgram.GatheringProvider);
            MauiProgram.GatheringSessionScope = sessionScope;

            var sessionService = sessionScope.ServiceProvider.GetRequiredService<IGatheringSessionService>();
            MauiProgram.Services?.GetRequiredService<IGatheringLiveState>().Attach(sessionService);
        }
        catch (Exception ex)
        {
            if (MauiProgram.AppDataPath is not null)
            {
                var logPath = Path.Combine(MauiProgram.AppDataPath, "debug_maui_startup_failures.log");
                await File.AppendAllTextAsync(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {ex.GetType().Name}: {ex.Message}{Environment.NewLine}");
            }
        }
    }
}
