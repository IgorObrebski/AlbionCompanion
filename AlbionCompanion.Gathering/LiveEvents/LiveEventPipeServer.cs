using System.IO.Pipes;
using AlbionCompanion.Core.Models;

namespace AlbionCompanion.Gathering.LiveEvents;

// Runs inside AlbionCompanion.Service. Stays alive for the whole Service lifetime (accepting
// connections) independent of whether the gathering pipeline is currently running - AttachSource/
// DetachSource is how Worker (Task 12) plugs the pipeline's IGatheringSessionService in and out as
// Albion Online starts/stops, without the pipe connections themselves dropping.
public class LiveEventPipeServer
{
    private readonly string _pipeName;
    private readonly ICharacterService _characterService;
    private readonly List<StreamWriter> _writers = new();
    private readonly object _writersLock = new();
    private IGatheringLiveEventSource? _source;

    public LiveEventPipeServer(string pipeName, ICharacterService characterService)
    {
        _pipeName = pipeName;
        _characterService = characterService;
    }

    public void AttachSource(IGatheringLiveEventSource source)
    {
        _source = source;
        source.OnSessionStarted += (_, s) => Broadcast(new SessionStartedMessage(s.Id, s.StartLocation, s.CharacterId));
        source.OnSessionEnded += (_, s) => Broadcast(new SessionEndedMessage(s.Id));
        source.OnLocationChanged += (_, s) => Broadcast(new LocationChangedMessage(s.Id, s.CurrentLocation));
        source.OnItemAdded += (_, i) => Broadcast(new ItemAddedMessage(i.ItemId, i.Amount, i.Location));
        source.OnFameAdded += (_, f) => Broadcast(new FameAddedMessage(f.Amount, f.Location));
        source.OnSilverAdded += (_, s) => Broadcast(new SilverAddedMessage(s.Amount, s.Location));
    }

    public void DetachSource() => _source = null;

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
            var writer = new StreamWriter(pipe) { AutoFlush = true };
            lock (_writersLock)
            {
                _writers.Add(writer);
            }

            _ = HandleClientAsync(pipe, writer, cancellationToken);
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, StreamWriter writer, CancellationToken cancellationToken)
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

            pipe.Dispose();
        }
    }

    private void Broadcast(LiveEventMessage message)
    {
        var line = LiveEventMessageSerializer.Serialize(message);
        List<StreamWriter> snapshot;
        lock (_writersLock)
        {
            snapshot = new List<StreamWriter>(_writers);
        }

        foreach (var writer in snapshot)
        {
            // Fire-and-forget the actual write: the pipe was opened with PipeOptions.Asynchronous,
            // and calling the synchronous StreamWriter.WriteLine on top of an async-mode pipe
            // handle can block the calling thread indefinitely on Windows. WriteLineAsync uses the
            // pipe's real async I/O path instead. Broadcast itself stays synchronous because it's
            // invoked directly from IGatheringLiveEventSource's synchronous EventHandler<T> events.
            _ = WriteLineAsync(writer, line);
        }
    }

    private static async Task WriteLineAsync(StreamWriter writer, string line)
    {
        try
        {
            await writer.WriteLineAsync(line);
        }
        catch (IOException)
        {
            // A dead client is cleaned up by its own HandleClientAsync loop noticing the
            // broken pipe on its next read - nothing to do here but skip this one write.
        }
    }
}
