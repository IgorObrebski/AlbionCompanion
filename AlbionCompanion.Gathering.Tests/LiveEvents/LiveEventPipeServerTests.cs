using System.IO.Pipes;
using AlbionCompanion.Core.Models;
using AlbionCompanion.Gathering.LiveEvents;
using Xunit;

namespace AlbionCompanion.Gathering.Tests.LiveEvents;

public class LiveEventPipeServerTests
{
    private sealed class FakeCharacterService : ICharacterService
    {
        public int NotifyCount { get; private set; }
        public event EventHandler? CharactersChanged;
        public Task<IReadOnlyList<Character>> GetAllAsync() => Task.FromResult<IReadOnlyList<Character>>(Array.Empty<Character>());
        public Task<Character> AddAsync(string name) => throw new NotImplementedException();
        public Task DeleteAsync(Guid id) => throw new NotImplementedException();
        public Task RenameAsync(Guid id, string newName) => throw new NotImplementedException();
        public Task<IReadOnlyList<CharacterOverview>> GetAllOverviewsAsync() => throw new NotImplementedException();
        public Task<CharacterOverview?> GetOverviewAsync(Guid characterId) => throw new NotImplementedException();
        public void NotifyCharactersChanged() { NotifyCount++; CharactersChanged?.Invoke(this, EventArgs.Empty); }
    }

    private sealed class FakeEventSource : IGatheringLiveEventSource
    {
        public event EventHandler<GatheringSession>? OnSessionStarted;
        public event EventHandler<GatheringSession>? OnSessionEnded;
        public event EventHandler<GatheringSession>? OnLocationChanged;
        public event EventHandler<GatheredItem>? OnItemAdded;
        public event EventHandler<FameLog>? OnFameAdded;
        public event EventHandler<SilverLog>? OnSilverAdded;

        public void RaiseSessionStarted(GatheringSession session) => OnSessionStarted?.Invoke(this, session);
        public void RaiseItemAdded(GatheredItem item) => OnItemAdded?.Invoke(this, item);
        public void RaiseFameAdded(FameLog fame) => OnFameAdded?.Invoke(this, fame);
    }

    [Fact]
    public async Task ConnectedClient_ReceivesSessionStartedMessage()
    {
        var pipeName = "TestPipe_" + Guid.NewGuid();
        var characterService = new FakeCharacterService();
        var server = new LiveEventPipeServer(pipeName, characterService);
        var source = new FakeEventSource();
        server.AttachSource(source);
        using var cts = new CancellationTokenSource();
        var serverTask = server.RunAsync(cts.Token);

        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync((int)TimeSpan.FromSeconds(5).TotalMilliseconds);
        using var reader = new StreamReader(client);

        var characterId = Guid.NewGuid();
        var session = new GatheringSession { StartLocation = "Martlock", CharacterId = characterId };
        source.RaiseSessionStarted(session);

        var readTask = reader.ReadLineAsync();
        var line = await readTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotNull(line);
        var message = Assert.IsType<SessionStartedMessage>(LiveEventMessageSerializer.Deserialize(line!));
        Assert.Equal("Martlock", message.StartLocation);
        Assert.Equal(characterId, message.CharacterId);

        cts.Cancel();
    }

    [Fact]
    public async Task ClientSendingCharacterRegistryChanged_NotifiesCharacterService()
    {
        var pipeName = "TestPipe_" + Guid.NewGuid();
        var characterService = new FakeCharacterService();
        var server = new LiveEventPipeServer(pipeName, characterService);
        using var cts = new CancellationTokenSource();
        var serverTask = server.RunAsync(cts.Token);

        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync((int)TimeSpan.FromSeconds(5).TotalMilliseconds);
        using var writer = new StreamWriter(client) { AutoFlush = true };

        await writer.WriteLineAsync(LiveEventMessageSerializer.Serialize(new CharacterRegistryChangedMessage()));

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (characterService.NotifyCount == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        Assert.Equal(1, characterService.NotifyCount);

        cts.Cancel();
    }

    [Fact]
    public async Task RapidFireEvents_DoNotInterleaveOnTheSameClientPipe()
    {
        var pipeName = "TestPipe_" + Guid.NewGuid();
        var characterService = new FakeCharacterService();
        var server = new LiveEventPipeServer(pipeName, characterService);
        var source = new FakeEventSource();
        server.AttachSource(source);
        using var cts = new CancellationTokenSource();
        var serverTask = server.RunAsync(cts.Token);

        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync((int)TimeSpan.FromSeconds(5).TotalMilliseconds);
        using var reader = new StreamReader(client);

        const int eventPairs = 50;
        for (var i = 0; i < eventPairs; i++)
        {
            // Fire two different events back-to-back with no await between them, exactly the
            // "OnItemAdded then OnFameAdded on the same tick" scenario the review flagged - each
            // call synchronously starts a fire-and-forget write against the same client writer.
            source.RaiseItemAdded(new GatheredItem { ItemId = "T" + i, Amount = 1, Location = "Loc" });
            source.RaiseFameAdded(new FameLog { Amount = i, Location = "Loc" });
        }

        for (var i = 0; i < eventPairs; i++)
        {
            var itemLine = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
            var fameLine = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));

            // If writes had interleaved their bytes on the shared StreamWriter, these lines would
            // either fail to deserialize (corrupted JSON) or deserialize to the wrong message type
            // / wrong ordinal - either failure proves the two events' bytes got mixed together.
            var itemMessage = Assert.IsType<ItemAddedMessage>(LiveEventMessageSerializer.Deserialize(itemLine!));
            Assert.Equal("T" + i, itemMessage.ItemId);

            var fameMessage = Assert.IsType<FameAddedMessage>(LiveEventMessageSerializer.Deserialize(fameLine!));
            Assert.Equal(i, fameMessage.Amount);
        }

        cts.Cancel();
    }

    [Fact]
    public async Task DetachSourceThenAttachNewSource_OnlyDeliversEventsFromTheNewSource()
    {
        var pipeName = "TestPipe_" + Guid.NewGuid();
        var characterService = new FakeCharacterService();
        var server = new LiveEventPipeServer(pipeName, characterService);
        var oldSource = new FakeEventSource();
        server.AttachSource(oldSource);
        server.DetachSource();

        var newSource = new FakeEventSource();
        server.AttachSource(newSource);

        using var cts = new CancellationTokenSource();
        var serverTask = server.RunAsync(cts.Token);

        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync((int)TimeSpan.FromSeconds(5).TotalMilliseconds);
        using var reader = new StreamReader(client);

        // The old (detached) source firing an event must not produce any message on the pipe.
        var oldSession = new GatheringSession { StartLocation = "OldSource", CharacterId = Guid.NewGuid() };
        oldSource.RaiseSessionStarted(oldSession);

        // The new source firing the same event must be the one and only message received.
        var newCharacterId = Guid.NewGuid();
        var newSession = new GatheringSession { StartLocation = "NewSource", CharacterId = newCharacterId };
        newSource.RaiseSessionStarted(newSession);

        var line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotNull(line);
        var message = Assert.IsType<SessionStartedMessage>(LiveEventMessageSerializer.Deserialize(line!));
        Assert.Equal("NewSource", message.StartLocation);
        Assert.Equal(newCharacterId, message.CharacterId);

        // No further message should ever arrive (in particular, nothing from oldSource) - confirm
        // the pipe goes quiet rather than delivering a second, unexpected line.
        var nextReadTask = reader.ReadLineAsync();
        var completed = await Task.WhenAny(nextReadTask, Task.Delay(TimeSpan.FromMilliseconds(300)));
        Assert.NotSame(nextReadTask, completed);

        cts.Cancel();
    }
}
