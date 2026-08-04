using AlbionCompanion.Gathering;
using AlbionCompanion.Gathering.LiveEvents;
using AlbionCompanion.Sniffer.PacketCapture;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AlbionCompanion.Service;

// Registered as always-Running/Automatic in the SCM, but internally idles between polls when
// Albion Online isn't running - see docs/superpowers/specs/2026-08-04-background-sniffer-service-design.md's
// "Process gating" section. GameCheckInterval matches that spec's 10-15s guidance.
public class Worker : BackgroundService
{
    private static readonly TimeSpan GameCheckInterval = TimeSpan.FromSeconds(15);
    private static readonly string ProgramDataPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AlbionCompanion");

    private readonly IGameProcessWatcher _gameProcessWatcher;
    private readonly ILogger<Worker> _logger;
    private readonly LiveEventPipeServer _pipeServer;
    private readonly ServiceProvider _statusServiceProvider;
    private ServiceProvider? _pipelineProvider;
    private IServiceScope? _pipelineScope;

    // protected (not private) so a test subclass overriding StartPipelineAsync/StopPipeline can
    // still flip it, letting WorkerGatingTests assert on IsPipelineRunning as the observable
    // side effect of a gating cycle without touching any real I/O.
    protected bool _pipelineRunning;

    // Exposed for tests: true whenever the last completed gating cycle left the pipeline running.
    // Production code should prefer the private _pipelineRunning field; this exists purely so
    // WorkerGatingTests can assert on an observable side effect without reaching into private state
    // or standing up a real capture pipeline.
    public bool IsPipelineRunning => _pipelineRunning;

    // gameProcessWatcher is injected (defaulting to a real GameProcessWatcher only in Program.cs's
    // composition) rather than hardcoded here, so tests can construct a Worker against a fake and
    // actually exercise the gating logic in ExecuteAsync instead of only testing the fake itself.
    public Worker(IGameProcessWatcher gameProcessWatcher, ILogger<Worker> logger)
    {
        _gameProcessWatcher = gameProcessWatcher;
        _logger = logger;
        Directory.CreateDirectory(ProgramDataPath);
        // The pipe server's own ICharacterService instance is separate from the gathering
        // pipeline's - it's only a placeholder here (used while no pipeline is attached) and must
        // stay alive across pipeline start/stop cycles (unlike the pipeline's own scoped services),
        // so it gets a small dedicated provider. StartPipelineAsync below swaps in the pipeline's
        // OWN ICharacterService instance via AttachSource once a pipeline is actually running -
        // that's the instance LocalPlayerTracker's cache-invalidation subscription actually cares
        // about; this placeholder has no listeners and only exists so the constructor has something
        // to pass. Kept as a field (not a local) so it can be disposed alongside the pipeline's own
        // provider on shutdown - it owns real IDisposable resources (HttpClient,
        // IDbContextFactory<AppDbContext>, IPacketSniffer, NpcapInstaller, etc.) via
        // AppHostBuilder.BuildServiceProvider, and outliving the process without disposal
        // would leak them for the whole service lifetime.
        _statusServiceProvider = AppHostBuilder.BuildServiceProvider(ProgramDataPath);
        _pipeServer = new LiveEventPipeServer("AlbionCompanionLiveEvents", _statusServiceProvider.GetRequiredService<ICharacterService>());
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Migration + WAL pragma + item-dictionary seed must happen once, here, independent of
        // whether Albion Online is running - it used to only run inside the game-gated
        // StartPipelineAsync, which left albion.db never created/migrated on a fresh machine until
        // the game was launched, breaking the App's CharacterHub/Sessions pages the whole time.
        try
        {
            await AppHostBuilder.RunDatabaseStartupAsync(_statusServiceProvider);
            _logger.LogInformation("Database startup sequence (migrate/WAL/seed) completed.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Database startup sequence failed - the App may not be able to load characters/sessions until the Service is restarted successfully.");
        }

        // Fire-and-forget by design (the accept loop runs for the whole service lifetime), but
        // observed: if the accept loop itself throws (e.g. the pipe name is already in use by a
        // stale instance), log it instead of silently never accepting any client with no signal.
        _ = _pipeServer.RunAsync(stoppingToken).ContinueWith(t =>
        {
            if (t.Exception is { } ex)
            {
                _logger.LogError(ex, "LiveEventPipeServer.RunAsync terminated unexpectedly - no App clients can connect until the Service is restarted.");
            }
        }, TaskContinuationOptions.OnlyOnFaulted);

        while (!stoppingToken.IsCancellationRequested)
        {
            await RunGatingCycleAsync();

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

    // One check-and-act cycle of the gating loop above, pulled out so tests can drive it directly
    // (toggling a fake IGameProcessWatcher and calling this repeatedly) instead of waiting on
    // ExecuteAsync's real Task.Delay(GameCheckInterval) between checks. virtual so a test subclass
    // can override StartPipelineAsync/StopPipeline to observe calls without touching Npcap/real
    // packet capture/the real database.
    internal async Task RunGatingCycleAsync()
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
    }

    protected virtual async Task StartPipelineAsync()
    {
        // Guarded: BackgroundService's default behavior on an unhandled exception from
        // ExecuteAsync is to stop the whole host - if Npcap/capture/DB-lock/anything here throws,
        // that used to take down the entire Windows Service process with no retry. Instead, log and
        // leave _pipelineRunning false so the next poll cycle just tries again.
        var provider = AppHostBuilder.BuildServiceProvider(ProgramDataPath);
        try
        {
            var scope = await AppHostBuilder.RunStartupSequenceAsync(provider);
            var sessionService = scope.ServiceProvider.GetRequiredService<IGatheringSessionService>();
            var pipelineCharacterService = provider.GetRequiredService<ICharacterService>();
            _pipeServer.AttachSource(sessionService, pipelineCharacterService);

            _pipelineProvider = provider;
            _pipelineScope = scope;
            _pipelineRunning = true;
            _logger.LogInformation("Gathering pipeline started (Albion Online detected running).");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start the gathering pipeline - will retry on the next poll.");
            // Partial failure: provider was built above but the pipeline never finished starting -
            // dispose it now instead of leaking it, since _pipelineProvider was never assigned.
            await provider.DisposeAsync();
        }
    }

    protected virtual void StopPipeline()
    {
        try
        {
            _pipeServer.DetachSource();
            _pipelineProvider?.GetRequiredService<IPacketSniffer>().Stop();
            _pipelineScope?.Dispose();
            _pipelineProvider?.Dispose();
            _logger.LogInformation("Gathering pipeline stopped (Albion Online no longer running).");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error while stopping the gathering pipeline - continuing shutdown anyway.");
        }
        finally
        {
            _pipelineProvider = null;
            _pipelineScope = null;
            _pipelineRunning = false;
        }
    }

    public override void Dispose()
    {
        // ExecuteAsync already calls StopPipeline() on a clean cancellation, but Dispose is the
        // one place guaranteed to run on every shutdown path (including if the host tears the
        // service down before/without ExecuteAsync's own cleanup running) - StopPipeline is a
        // no-op past the first call (_pipelineProvider/_pipelineScope are already null'd out), so
        // calling it again here is harmless. _statusServiceProvider has no such guard elsewhere,
        // so this is the only place it gets disposed.
        if (_pipelineRunning)
        {
            StopPipeline();
        }

        _statusServiceProvider.Dispose();
        base.Dispose();
    }
}
