using AlbionCompanion.Gathering;
using AlbionCompanion.Gathering.LiveEvents;
using AlbionCompanion.Sniffer.PacketCapture;
using Microsoft.Extensions.DependencyInjection;

namespace AlbionCompanion.Service;

// Registered as always-Running/Automatic in the SCM, but internally idles between polls when
// Albion Online isn't running - see docs/superpowers/specs/2026-08-04-background-sniffer-service-design.md's
// "Process gating" section. GameCheckInterval matches that spec's 10-15s guidance.
public class Worker : BackgroundService
{
    private static readonly TimeSpan GameCheckInterval = TimeSpan.FromSeconds(15);
    private static readonly string ProgramDataPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AlbionCompanion");

    private readonly IGameProcessWatcher _gameProcessWatcher = new GameProcessWatcher();
    private readonly LiveEventPipeServer _pipeServer;
    private ServiceProvider? _pipelineProvider;
    private IServiceScope? _pipelineScope;
    private bool _pipelineRunning;

    public Worker()
    {
        Directory.CreateDirectory(ProgramDataPath);
        // The pipe server's own ICharacterService instance is separate from the gathering
        // pipeline's - it only needs write-free read access for NotifyCharactersChanged's
        // side-effect-free re-raise, and must stay alive across pipeline start/stop cycles
        // (unlike the pipeline's own scoped services), so it gets a small dedicated provider.
        var statusProvider = AppHostBuilder.BuildServiceProvider(ProgramDataPath);
        _pipeServer = new LiveEventPipeServer("AlbionCompanionLiveEvents", statusProvider.GetRequiredService<ICharacterService>());
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _ = _pipeServer.RunAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var gameRunning = _gameProcessWatcher.IsGameRunning();

            if (gameRunning && !_pipelineRunning)
            {
                await StartPipelineAsync();
            }
            else if (!gameRunning && _pipelineRunning)
            {
                StopPipeline();
            }

            try
            {
                await Task.Delay(GameCheckInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        if (_pipelineRunning)
        {
            StopPipeline();
        }
    }

    private async Task StartPipelineAsync()
    {
        _pipelineProvider = AppHostBuilder.BuildServiceProvider(ProgramDataPath);
        _pipelineScope = await AppHostBuilder.RunStartupSequenceAsync(_pipelineProvider);
        var sessionService = _pipelineScope.ServiceProvider.GetRequiredService<IGatheringSessionService>();
        _pipeServer.AttachSource(sessionService);
        _pipelineRunning = true;
    }

    private void StopPipeline()
    {
        _pipeServer.DetachSource();
        _pipelineProvider?.GetRequiredService<IPacketSniffer>().Stop();
        _pipelineScope?.Dispose();
        _pipelineProvider?.Dispose();
        _pipelineProvider = null;
        _pipelineScope = null;
        _pipelineRunning = false;
    }
}
