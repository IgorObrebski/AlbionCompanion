using AlbionCompanion.Core.Data;
using AlbionCompanion.Sniffer.Protocol16;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AlbionCompanion.Gathering.Tests;

public class GatheringEventRouterTests
{
    private sealed class FakePhotonParser : IPhotonParser
    {
        public event EventHandler<PhotonEvent>? OnEventReceived;
        public event EventHandler<PhotonResponse>? OnResponseReceived;
        public event EventHandler<PhotonRequest>? OnRequestReceived;
        public void HandlePayload(byte[] payload) { }
        public void RaiseEvent(PhotonEvent photonEvent) => OnEventReceived?.Invoke(this, photonEvent);
    }

    private sealed class FakeLocalPlayerTracker : ILocalPlayerTracker
    {
        public int? CurrentEntityId { get; set; }
    }

    private sealed class FakeHarvestableNodeTracker : IHarvestableNodeTracker
    {
        private readonly Dictionary<int, int> _tierByNodeId;

        public FakeHarvestableNodeTracker(Dictionary<int, int>? tierByNodeId = null) => _tierByNodeId = tierByNodeId ?? new Dictionary<int, int>();

        public int? GetTier(int nodeId) => _tierByNodeId.TryGetValue(nodeId, out var tier) ? tier : null;
    }

    private static (GatheringSessionService Service, AppDbContext Context) CreateServiceWithOpenSession(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        var context = new AppDbContext(options);
        context.Database.EnsureCreated();
        var service = new GatheringSessionService(context);
        return (service, context);
    }

    private const int LocalPlayerEntityId = 535802;
    private const int NodeId = 2955;

    private static PhotonEvent HarvestStart(int actorEntityId, int categoryCode, int nodeId = NodeId) =>
        new(1, new Dictionary<byte, object?> { [0] = actorEntityId, [3] = nodeId, [4] = categoryCode, [252] = (byte)59 });

    [Fact]
    public async Task HarvestStartEvent_WithKnownTierAndCategory_AddsFullyResolvedItemId()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var (service, context) = CreateServiceWithOpenSession(connection);
        await service.StartSessionAsync("4213");
        var parser = new FakePhotonParser();
        var localPlayer = new FakeLocalPlayerTracker { CurrentEntityId = LocalPlayerEntityId };
        var nodeTracker = new FakeHarvestableNodeTracker(new Dictionary<int, int> { [NodeId] = 4 });
        var router = new GatheringEventRouter(parser, service, localPlayer, nodeTracker);

        // categoryCode=27 (ORE, per HarvestableCategory), node tier=4 -> "T4_ORE".
        // Layout confirmed against live captures.
        await router.HandleEventAsync(HarvestStart(actorEntityId: LocalPlayerEntityId, categoryCode: 27));

        var item = Assert.Single(context.GatheredItems);
        Assert.Equal("T4_ORE", item.ItemId);
        Assert.Equal(1, item.Amount);
    }

    [Fact]
    public async Task HarvestStartEvent_WithUnknownTier_FallsBackToBareCategoryCode()
    {
        // Regression: this is exactly the bug the player caught - three different tiers of Ore
        // (Iron/Tin/Titanium) all share category code 27, so without a resolved tier they'd be
        // indistinguishable. The fallback should still record *something* rather than dropping
        // the swing, but must not fabricate a tier it doesn't have.
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var (service, context) = CreateServiceWithOpenSession(connection);
        await service.StartSessionAsync("4213");
        var parser = new FakePhotonParser();
        var localPlayer = new FakeLocalPlayerTracker { CurrentEntityId = LocalPlayerEntityId };
        var nodeTracker = new FakeHarvestableNodeTracker(); // no tier known for this node
        var router = new GatheringEventRouter(parser, service, localPlayer, nodeTracker);

        await router.HandleEventAsync(HarvestStart(actorEntityId: LocalPlayerEntityId, categoryCode: 27));

        var item = Assert.Single(context.GatheredItems);
        Assert.Equal("27", item.ItemId);
    }

    [Fact]
    public async Task HarvestStartEvent_ByAnotherNearbyPlayer_IsIgnored()
    {
        // Regression: HarvestStart is broadcast to everyone in the zone, not just the local
        // player. A live capture showed two other players' harvest swings (different item
        // types) recorded into the local player's own session before this filter existed.
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var (service, context) = CreateServiceWithOpenSession(connection);
        await service.StartSessionAsync("4213");
        var parser = new FakePhotonParser();
        var localPlayer = new FakeLocalPlayerTracker { CurrentEntityId = LocalPlayerEntityId };
        var router = new GatheringEventRouter(parser, service, localPlayer, new FakeHarvestableNodeTracker());

        await router.HandleEventAsync(HarvestStart(actorEntityId: 448437, categoryCode: 7));

        Assert.Empty(context.GatheredItems);
    }

    [Fact]
    public async Task HarvestStartEvent_WithUnknownLocalEntityId_IsIgnored()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var (service, context) = CreateServiceWithOpenSession(connection);
        await service.StartSessionAsync("4213");
        var parser = new FakePhotonParser();
        var localPlayer = new FakeLocalPlayerTracker { CurrentEntityId = null };
        var router = new GatheringEventRouter(parser, service, localPlayer, new FakeHarvestableNodeTracker());

        await router.HandleEventAsync(HarvestStart(actorEntityId: LocalPlayerEntityId, categoryCode: 27));

        Assert.Empty(context.GatheredItems);
    }

    [Fact]
    public async Task RepeatedHarvestStartEvents_AccumulateSeparateGatheredItemEntries()
    {
        // Regression: a partial-depletion node (e.g. starting at 2/5 charges, ending at 0/5)
        // must still record every swing, not just a single "finished" tally.
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var (service, context) = CreateServiceWithOpenSession(connection);
        await service.StartSessionAsync("4213");
        var parser = new FakePhotonParser();
        var localPlayer = new FakeLocalPlayerTracker { CurrentEntityId = LocalPlayerEntityId };
        var nodeTracker = new FakeHarvestableNodeTracker(new Dictionary<int, int> { [NodeId] = 4 });
        var router = new GatheringEventRouter(parser, service, localPlayer, nodeTracker);

        for (var i = 0; i < 3; i++)
        {
            await router.HandleEventAsync(HarvestStart(actorEntityId: LocalPlayerEntityId, categoryCode: 27));
        }

        Assert.Equal(3, context.GatheredItems.Count());
        Assert.Equal(3, context.GatheredItems.Sum(item => item.Amount));
    }

    [Fact]
    public async Task OtherSemanticEventCode_IsIgnored()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var (service, context) = CreateServiceWithOpenSession(connection);
        await service.StartSessionAsync("4213");
        var parser = new FakePhotonParser();
        var localPlayer = new FakeLocalPlayerTracker { CurrentEntityId = LocalPlayerEntityId };
        var router = new GatheringEventRouter(parser, service, localPlayer, new FakeHarvestableNodeTracker());

        await router.HandleEventAsync(new PhotonEvent(1,
            new Dictionary<byte, object?> { [0] = 437975, [252] = (byte)3 })); // Move

        Assert.Empty(context.GatheredItems);
    }

    [Fact]
    public async Task HarvestStartEvent_WithNoActiveSession_IsIgnored()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var (service, context) = CreateServiceWithOpenSession(connection);
        var parser = new FakePhotonParser();
        var localPlayer = new FakeLocalPlayerTracker { CurrentEntityId = LocalPlayerEntityId };
        var router = new GatheringEventRouter(parser, service, localPlayer, new FakeHarvestableNodeTracker());

        await router.HandleEventAsync(HarvestStart(actorEntityId: LocalPlayerEntityId, categoryCode: 27));

        Assert.Empty(context.GatheredItems);
    }
}
