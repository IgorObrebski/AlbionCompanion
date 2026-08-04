using System.IO.Pipes;
using AlbionCompanion.Core.Models;

namespace AlbionCompanion.Gathering.LiveEvents;

// Runs inside the App. Connects out to LiveEventPipeServer (hosted in AlbionCompanion.Service) and
// implements IGatheringLiveEventSource so GatheringLiveState can subscribe to it exactly like it
// subscribes to IGatheringSessionService - the App doesn't need to know whether the events came
// from an in-process pipeline or a named pipe.
public class LiveEventPipeClient : IGatheringLiveEventSource, IDisposable
{
    public enum ConnectionStatus { Disconnected, Connecting, Connected, Exhausted }

    private const int MaxAttempts = 5;

    // 3000ms matches the brief's original intent for real usage: RetryNowAsync exists so the
    // Settings page's "start service" button can retry right after the Windows Service process
    // has just been launched - at that moment the named pipe may already exist but the server
    // isn't yet actively blocked in WaitForConnectionAsync, so a real ConnectAsync can legitimately
    // need to wait rather than fail instantly. This must NOT be derived from retryDelay - the two
    // are independent concerns (how patient a single connection attempt is, versus how long to
    // wait between attempts), and coupling them previously meant a test-sized retryDelay silently
    // starved production-sized connect attempts of their patience. Tests that need to run fast
    // pass a short connectTimeout explicitly, the same way they already override retryDelay.
    private static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromMilliseconds(3000);

    private readonly string _pipeName;
    private readonly TimeSpan _retryDelay;
    private readonly TimeSpan _connectTimeout;
    private NamedPipeClientStream? _pipe;
    private StreamWriter? _writer;
    private bool _disposed;
    private int _connectingGuard;

    public event EventHandler<GatheringSession>? OnSessionStarted;
    public event EventHandler<GatheringSession>? OnSessionEnded;
    public event EventHandler<GatheringSession>? OnLocationChanged;
    public event EventHandler<GatheredItem>? OnItemAdded;
    public event EventHandler<FameLog>? OnFameAdded;
    public event EventHandler<SilverLog>? OnSilverAdded;
    public event EventHandler? OnStatusChanged;

    public ConnectionStatus Status { get; private set; } = ConnectionStatus.Disconnected;

    public LiveEventPipeClient(string pipeName, TimeSpan? retryDelay = null, TimeSpan? connectTimeout = null)
    {
        _pipeName = pipeName;
        _retryDelay = retryDelay ?? TimeSpan.FromSeconds(3);
        _connectTimeout = connectTimeout ?? DefaultConnectTimeout;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // No-op if already connected: without this guard, a stale UI click (e.g. Settings' retry
        // button clicked twice in quick succession) could call StartAsync/RetryNowAsync while a
        // connection is already live, and TryConnectAsync below would overwrite _pipe/_writer
        // without disposing the previous ones - leaking the old pipe and starting a second
        // ReadLoopAsync concurrently with the first.
        if (Status == ConnectionStatus.Connected)
        {
            return;
        }

        // Guard against overlapping retry cycles - e.g. Settings' retry button clicked twice in
        // quick succession, or the auto-reconnect kicked off from ReadLoopAsync's finally block
        // racing a manual RetryNowAsync. Only one cycle runs at a time; a second caller just no-ops.
        if (Interlocked.CompareExchange(ref _connectingGuard, 1, 0) != 0)
        {
            return;
        }

        try
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
        finally
        {
            // Only release the guard if this call still actually owns it. TryConnectAsync now
            // releases the guard itself right after a successful connect (before starting the read
            // loop), so by the time we get here on the success path the guard may already have been
            // taken by a brand-new reconnect cycle kicked off from ReadLoopAsync's own finally after
            // an immediate drop. An unconditional Exchange(0) here would stomp that new cycle's
            // ownership of the guard, letting a third, uncoordinated caller slip in concurrently with
            // it - the same class of bug in a narrower spot. CompareExchange(0, 1) only clears the
            // guard if it is still exactly the value this call itself set it to.
            Interlocked.CompareExchange(ref _connectingGuard, 0, 1);
        }
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

            // Dispose() may have run while ConnectAsync was still in flight. Without this check, a
            // connect completing after disposal would still assign _pipe/_writer and start a read
            // loop on an object the caller believes is already torn down.
            if (_disposed)
            {
                pipe.Dispose();
                return false;
            }

            _pipe = pipe;
            _writer = new StreamWriter(pipe) { AutoFlush = true };
            SetStatus(ConnectionStatus.Connected);

            // Release the connecting guard BEFORE starting the read loop, not after StartAsync's own
            // finally block runs. ReadLoopAsync runs as a separately scheduled fire-and-forget task;
            // if the connection drops immediately (e.g. the Service process bouncing right after
            // accepting), ReadLoopAsync's own finally can race ahead of StartAsync's unwind-and-return
            // and try to kick off a fresh retry cycle while the guard is still held by the outer
            // StartAsync call - which would silently no-op and leave the client stuck in Disconnected
            // forever. Releasing here guarantees the guard is free before the read loop can possibly
            // observe a drop, so a genuine reconnect-after-drop always gets to run.
            Interlocked.Exchange(ref _connectingGuard, 0);
            _ = ReadLoopAsync(pipe, cancellationToken);
            return true;
        }
        catch (Exception)
        {
            // Intentionally broad: NamedPipeClientStream.ConnectAsync's failure modes for "this
            // attempt failed" - TimeoutException (no listener within connectTimeout),
            // IOException (pipe busy / all server instances taken), and OperationCanceledException
            // (caller cancelled) - are all treated identically here, letting the caller's retry
            // loop decide whether to try again. Dispose the half-opened pipe instead of leaking it
            // (ConnectAsync can throw after allocating OS resources).
            pipe?.Dispose();
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _writer?.Dispose();
        _pipe?.Dispose();
        _writer = null;
        _pipe = null;
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

            // The design spec requires the retry loop to run "when the App launches AND whenever a
            // connection drops" - previously this finally block only flipped status to Disconnected
            // and nothing restarted the retry cycle, leaving the App stuck there forever (short of
            // restarting the App) if the Service ever bounced. Kick off a fresh retry cycle here,
            // unless the client itself was disposed or the caller's own token was cancelled (both
            // mean the App is shutting this connection down on purpose, not recovering from a drop).
            if (!_disposed && !cancellationToken.IsCancellationRequested)
            {
                _ = StartAsync(cancellationToken);
            }
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
