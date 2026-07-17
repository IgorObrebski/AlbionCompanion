using AlbionCompanion.Gathering;
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

        window.Destroying += (_, _) =>
        {
            MauiProgram.GatheringProvider?.GetRequiredService<AlbionCompanion.Sniffer.PacketCapture.IPacketSniffer>().Stop();
            MauiProgram.GatheringSessionScope?.Dispose();
        };

        _ = StartGatheringAsync();

        return window;
    }

    private static async Task StartGatheringAsync()
    {
        if (MauiProgram.GatheringProvider is null)
        {
            return;
        }

        var sessionScope = await AppHostBuilder.RunStartupSequenceAsync(MauiProgram.GatheringProvider);
        MauiProgram.GatheringSessionScope = sessionScope;

        var sessionService = sessionScope.ServiceProvider.GetRequiredService<IGatheringSessionService>();
        MauiProgram.Services?.GetRequiredService<IGatheringLiveState>().Attach(sessionService);
    }
}
