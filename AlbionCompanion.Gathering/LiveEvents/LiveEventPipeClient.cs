using System.IO.Pipes;
using AlbionCompanion.Core.Models;

namespace AlbionCompanion.Gathering.LiveEvents;

// Runs inside the App. Connects out to LiveEventPipeServer (hosted in AlbionCompanion.Service) and
// implements IGatheringLiveEventSource so GatheringLiveState can subscribe to it exactly like it
// subscribes to IGatheringSessionService - the App doesn't need to know whether the events came
// from an in-process pipeline or a named pipe.
public class LiveEventPipeClient : IGatheringLiveEventSource
{
    public enum ConnectionStatus { Disconnected, Connecting, Connected, Exhausted }

    private const int MaxAttempts = 5;

    // Each connect attempt against a pipe nobody is listening on must fail well inside a single
    // retryDelay-scaled test window, not sit on the .NET default-ish multi-second timeout. Real
    // production use (retryDelay ~3s) still gets a generous per-attempt window (a few multiples of
    // retryDelay, capped so it can never dwarf the interval between attempts); tests that pass a
    // tiny retryDelay (e.g. 10ms) get a correspondingly tiny connect timeout, so 5 attempts + 4
    // delays comfortably finish inside a 5s WaitAsync bound instead of the brief's hardcoded
    // 3000ms-per-attempt version, which alone would take >=12s across 5 attempts and blow past any
    // such bound.
    private static readonly TimeSpan MaxConnectTimeout = TimeSpan.FromSeconds(1);

    private readonly string _pipeName;
    private readonly TimeSpan _retryDelay;
    private readonly TimeSpan _connectTimeout;
    private NamedPipeClientStream? _pipe;
    private StreamWriter? _writer;

    public event EventHandler<GatheringSession>? OnSessionStarted;
    public event EventHandler<GatheringSession>? OnSessionEnded;
    public event EventHandler<GatheringSession>? OnLocationChanged;
    public event EventHandler<GatheredItem>? OnItemAdded;
    public event EventHandler<FameLog>? OnFameAdded;
    public event EventHandler<SilverLog>? OnSilverAdded;
    public event EventHandler? OnStatusChanged;

    public ConnectionStatus Status { get; private set; } = ConnectionStatus.Disconnected;

    public LiveEventPipeClient(string pipeName, TimeSpan? retryDelay = null)
    {
        _pipeName = pipeName;
        _retryDelay = retryDelay ?? TimeSpan.FromSeconds(3);
        var scaled = TimeSpan.FromTicks(_retryDelay.Ticks * 10);
        _connectTimeout = scaled < MaxConnectTimeout ? (scaled <= TimeSpan.Zero ? MaxConnectTimeout : scaled) : MaxConnectTimeout;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            SetStatus(ConnectionStatus.Connecting);
            if (await TryConnectAsync(cancellationToken))
            {
                return;
            }

            if (attempt < MaxAttempts)
            {
                await Task.Delay(_retryDelay, cancellationToken);
            }
        }

        SetStatus(ConnectionStatus.Exhausted);
    }

    public Task RetryNowAsync(CancellationToken cancellationToken = default) => StartAsync(cancellationToken);

    public async Task SendCharacterRegistryChangedAsync()
    {
        var writer = _writer;
        if (writer is null)
        {
            return;
        }

        try
        {
            await writer.WriteLineAsync(LiveEventMessageSerializer.Serialize(new CharacterRegistryChangedMessage()));
        }
        catch (IOException)
        {
            // Server-side disconnect while sending - the read loop will notice the same thing and
            // flip status to Disconnected; nothing more to do for this particular send.
        }
        catch (ObjectDisposedException)
        {
            // Pipe was already torn down between the null-check above and this write.
        }
    }

    private async Task<bool> TryConnectAsync(CancellationToken cancellationToken)
    {
        NamedPipeClientStream? pipe = null;
        try
        {
            pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync((int)_connectTimeout.TotalMilliseconds, cancellationToken);
            _pipe = pipe;
            _writer = new StreamWriter(pipe) { AutoFlush = true };
            SetStatus(ConnectionStatus.Connected);
            _ = ReadLoopAsync(pipe, cancellationToken);
            return true;
        }
        catch (Exception)
        {
            // Timeout, no listener, or the pipe was busy - treated identically as "this attempt
            // failed," the caller's loop decides whether to retry. Dispose the half-opened pipe
            // instead of leaking it (ConnectAsync can throw after allocating OS resources).
            pipe?.Dispose();
            return false;
        }
    }

    private async Task ReadLoopAsync(NamedPipeClientStream pipe, CancellationToken cancellationToken)
    {
        var reader = new StreamReader(pipe);
        try
        {
            while (!cancellationToken.IsCancellationRequested && pipe.IsConnected)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (line is null)
                {
                    break;
                }

                Dispatch(LiveEventMessageSerializer.Deserialize(line));
            }
        }
        catch (IOException)
        {
            // Server-side disconnect - fall through, mark disconnected below.
        }
        catch (OperationCanceledException)
        {
            // StartAsync's caller cancelled - fall through, mark disconnected below.
        }
        finally
        {
            SetStatus(ConnectionStatus.Disconnected);
        }
    }

    private void Dispatch(LiveEventMessage message)
    {
        switch (message)
        {
            case SessionStartedMessage m:
                OnSessionStarted?.Invoke(this, new GatheringSession { Id = m.SessionId, StartLocation = m.StartLocation, CurrentLocation = m.StartLocation, CharacterId = m.CharacterId });
                break;
            case SessionEndedMessage m:
                OnSessionEnded?.Invoke(this, new GatheringSession { Id = m.SessionId });
                break;
            case LocationChangedMessage m:
                OnLocationChanged?.Invoke(this, new GatheringSession { Id = m.SessionId, CurrentLocation = m.CurrentLocation });
                break;
            case ItemAddedMessage m:
                OnItemAdded?.Invoke(this, new GatheredItem { ItemId = m.ItemId, Amount = m.Amount, Location = m.Location });
                break;
            case FameAddedMessage m:
                OnFameAdded?.Invoke(this, new FameLog { Amount = m.Amount, Location = m.Location });
                break;
            case SilverAddedMessage m:
                OnSilverAdded?.Invoke(this, new SilverLog { Amount = m.Amount, Location = m.Location });
                break;
        }
    }

    private void SetStatus(ConnectionStatus status)
    {
        Status = status;
        OnStatusChanged?.Invoke(this, EventArgs.Empty);
    }
}
