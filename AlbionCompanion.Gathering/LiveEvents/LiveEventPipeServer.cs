using System.IO.Pipes;
using AlbionCompanion.Core.Models;

namespace AlbionCompanion.Gathering.LiveEvents;

// Runs inside AlbionCompanion.Service. Stays alive for the whole Service lifetime (accepting
// connections) independent of whether the gathering pipeline is currently running - AttachSource/
// DetachSource is how Worker (Task 12) plugs the pipeline's IGatheringSessionService in and out as
// Albion Online starts/stops, without the pipe connections themselves dropping.
public class LiveEventPipeServer
{
    // A StreamWriter is not thread-safe for concurrent writes from the same instance, and
    // NamedPipeServerStream can be disposed (client disconnected) while a write is in flight - this
    // wrapper pairs each writer with a 1-slot semaphore so Broadcast can serialize writes per client
    // and Dispose can wait for any in-flight write to finish before the pipe goes away.
    private sealed class ClientWriter
    {
        public StreamWriter Writer { get; }
        public SemaphoreSlim WriteLock { get; } = new(1, 1);

        public ClientWriter(StreamWriter writer) => Writer = writer;
    }

    private readonly string _pipeName;
    private readonly ICharacterService _characterService;
    private readonly List<ClientWriter> _writers = new();
    private readonly object _writersLock = new();
    private IGatheringLiveEventSource? _source;
    private EventHandler<GatheringSession>? _onSessionStarted;
    private EventHandler<GatheringSession>? _onSessionEnded;
    private EventHandler<GatheringSession>? _onLocationChanged;
    private EventHandler<GatheredItem>? _onItemAdded;
    private EventHandler<FameLog>? _onFameAdded;
    private EventHandler<SilverLog>? _onSilverAdded;

    public LiveEventPipeServer(string pipeName, ICharacterService characterService)
    {
        _pipeName = pipeName;
        _characterService = characterService;
    }

    public void AttachSource(IGatheringLiveEventSource source)
    {
        // Guard against double-subscription if a source is already attached (e.g. Worker calling
        // AttachSource again without an intervening DetachSource) - unsubscribe the old handlers
        // from the old source first.
        DetachSource();

        _source = source;
        _onSessionStarted = (_, s) => Broadcast(new SessionStartedMessage(s.Id, s.StartLocation, s.CharacterId));
        _onSessionEnded = (_, s) => Broadcast(new SessionEndedMessage(s.Id));
        _onLocationChanged = (_, s) => Broadcast(new LocationChangedMessage(s.Id, s.CurrentLocation));
        _onItemAdded = (_, i) => Broadcast(new ItemAddedMessage(i.ItemId, i.Amount, i.Location));
        _onFameAdded = (_, f) => Broadcast(new FameAddedMessage(f.Amount, f.Location));
        _onSilverAdded = (_, s) => Broadcast(new SilverAddedMessage(s.Amount, s.Location));

        source.OnSessionStarted += _onSessionStarted;
        source.OnSessionEnded += _onSessionEnded;
        source.OnLocationChanged += _onLocationChanged;
        source.OnItemAdded += _onItemAdded;
        source.OnFameAdded += _onFameAdded;
        source.OnSilverAdded += _onSilverAdded;
    }

    public void DetachSource()
    {
        if (_source is null)
        {
            return;
        }

        _source.OnSessionStarted -= _onSessionStarted;
        _source.OnSessionEnded -= _onSessionEnded;
        _source.OnLocationChanged -= _onLocationChanged;
        _source.OnItemAdded -= _onItemAdded;
        _source.OnFameAdded -= _onFameAdded;
        _source.OnSilverAdded -= _onSilverAdded;

        _source = null;
        _onSessionStarted = null;
        _onSessionEnded = null;
        _onLocationChanged = null;
        _onItemAdded = null;
        _onFameAdded = null;
        _onSilverAdded = null;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var pipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            try
            {
                await pipe.WaitForConnectionAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                pipe.Dispose();
                break;
            }

            // Register the writer synchronously, on this same accept loop, before returning to
            // WaitForConnectionAsync for the next client. If registration were deferred into the
            // HandleClientAsync task below (started fire-and-forget), a broadcast raised right
            // after the client's own ConnectAsync returns could race ahead of that task ever
            // getting scheduled, silently dropping the message and leaving the client's read
            // pending forever (and pending pipe disposal can then hang indefinitely).
            var clientWriter = new ClientWriter(new StreamWriter(pipe) { AutoFlush = true });
            lock (_writersLock)
            {
                _writers.Add(clientWriter);
            }

            _ = HandleClientAsync(pipe, clientWriter, cancellationToken);
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, ClientWriter writer, CancellationToken cancellationToken)
    {
        try
        {
            var reader = new StreamReader(pipe);
            while (!cancellationToken.IsCancellationRequested && pipe.IsConnected)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (line is null)
                {
                    break;
                }

                if (LiveEventMessageSerializer.Deserialize(line) is CharacterRegistryChangedMessage)
                {
                    _characterService.NotifyCharactersChanged();
                }
            }
        }
        catch (IOException)
        {
            // Client disconnected - fall through to cleanup below.
        }
        catch (OperationCanceledException)
        {
            // Service shutting down - fall through to cleanup below.
        }
        finally
        {
            lock (_writersLock)
            {
                _writers.Remove(writer);
            }

            // Wait for any write currently in flight against this client's writer before disposing
            // the pipe out from under it - otherwise a Broadcast racing this disconnect could throw
            // ObjectDisposedException mid-write instead of the expected IOException. Once acquired,
            // no new write can start (Broadcast's WriteLineAsync also acquires this lock, and the
            // writer has already been removed from _writers above so a *subsequent* Broadcast won't
            // even try).
            await writer.WriteLock.WaitAsync();
            try
            {
                pipe.Dispose();
            }
            finally
            {
                writer.WriteLock.Release();
            }
        }
    }

    private void Broadcast(LiveEventMessage message)
    {
        var line = LiveEventMessageSerializer.Serialize(message);
        List<ClientWriter> snapshot;
        lock (_writersLock)
        {
            snapshot = new List<ClientWriter>(_writers);
        }

        foreach (var writer in snapshot)
        {
            // Fire-and-forget the actual write: the pipe was opened with PipeOptions.Asynchronous,
            // and calling the synchronous StreamWriter.WriteLine on top of an async-mode pipe
            // handle can block the calling thread indefinitely on Windows. WriteLineAsync uses the
            // pipe's real async I/O path instead. Broadcast itself stays synchronous because it's
            // invoked directly from IGatheringLiveEventSource's synchronous EventHandler<T> events.
            // Writes to the same client are serialized via the writer's own WriteLock so two events
            // firing back-to-back (e.g. OnItemAdded then OnFameAdded on the same tick) can't
            // interleave their bytes on the same StreamWriter/pipe handle.
            _ = WriteLineAsync(writer, line);
        }
    }

    private static async Task WriteLineAsync(ClientWriter writer, string line)
    {
        await writer.WriteLock.WaitAsync();
        try
        {
            await writer.Writer.WriteLineAsync(line);
        }
        catch (IOException)
        {
            // A dead client is cleaned up by its own HandleClientAsync loop noticing the
            // broken pipe on its next read - nothing to do here but skip this one write.
        }
        catch (ObjectDisposedException)
        {
            // The client disconnected and HandleClientAsync's cleanup disposed the pipe between
            // this write being queued and it actually running - nothing to do here either.
        }
        finally
        {
            writer.WriteLock.Release();
        }
    }
}
