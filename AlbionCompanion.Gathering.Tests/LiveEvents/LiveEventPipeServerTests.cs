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
}
