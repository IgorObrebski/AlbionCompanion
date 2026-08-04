using AlbionCompanion.Core.Models;
using AlbionCompanion.Gathering;
using AlbionCompanion.Gathering.LiveEvents;
using Xunit;

namespace AlbionCompanion.Gathering.Tests.LiveEvents;

// Tasks 4 and 5 each tested their own side against a raw pipe stream. This test proves they
// correctly talk to *each other*, catching any protocol mismatch before Task 12 wires them into two
// separate real processes (where a bug would be much harder to diagnose).
public class LiveEventPipeIntegrationTests
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

        public void RaiseItemAdded(GatheredItem item) => OnItemAdded?.Invoke(this, item);
    }

    [Fact]
    public async Task ItemAddedOnServerSide_ArrivesOnClientSide()
    {
        var pipeName = "IntegrationTestPipe_" + Guid.NewGuid();
        var server = new LiveEventPipeServer(pipeName, new FakeCharacterService());
        var source = new FakeEventSource();
        server.AttachSource(source);
        using var cts = new CancellationTokenSource();
        _ = server.RunAsync(cts.Token);

        var client = new LiveEventPipeClient(pipeName, retryDelay: TimeSpan.FromMilliseconds(10));
        GatheredItem? received = null;
        var tcs = new TaskCompletionSource();
        client.OnItemAdded += (_, item) => { received = item; tcs.TrySetResult(); };
        await client.StartAsync(cts.Token);
        Assert.Equal(LiveEventPipeClient.ConnectionStatus.Connected, client.Status);

        source.RaiseItemAdded(new GatheredItem { ItemId = "T4_ORE", Amount = 5, Location = "Martlock" });
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotNull(received);
        Assert.Equal("T4_ORE", received!.ItemId);
        Assert.Equal(5, received.Amount);

        cts.Cancel();
    }

    [Fact]
    public async Task CharacterRegistryChangedFromClient_ReachesServersCharacterService()
    {
        var pipeName = "IntegrationTestPipe_" + Guid.NewGuid();
        var characterService = new FakeCharacterService();
        var server = new LiveEventPipeServer(pipeName, characterService);
        using var cts = new CancellationTokenSource();
        _ = server.RunAsync(cts.Token);

        var client = new LiveEventPipeClient(pipeName, retryDelay: TimeSpan.FromMilliseconds(10));
        await client.StartAsync(cts.Token);
        Assert.Equal(LiveEventPipeClient.ConnectionStatus.Connected, client.Status);

        await client.SendCharacterRegistryChangedAsync();

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (characterService.NotifyCount == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        Assert.Equal(1, characterService.NotifyCount);

        cts.Cancel();
    }
}
