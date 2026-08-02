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
        private readonly Dictionary<int, int> _enchantmentLevelByNodeId;
        private readonly Dictionary<int, int> _categoryCodeByNodeId;

        public FakeHarvestableNodeTracker(
            Dictionary<int, int>? tierByNodeId = null,
            Dictionary<int, int>? enchantmentLevelByNodeId = null,
            Dictionary<int, int>? categoryCodeByNodeId = null)
        {
            _tierByNodeId = tierByNodeId ?? new Dictionary<int, int>();
            _enchantmentLevelByNodeId = enchantmentLevelByNodeId ?? new Dictionary<int, int>();
            _categoryCodeByNodeId = categoryCodeByNodeId ?? new Dictionary<int, int>();
        }

        public int? GetTier(int nodeId) => _tierByNodeId.TryGetValue(nodeId, out var tier) ? tier : null;

        public int? GetEnchantmentLevel(int nodeId) =>
            _enchantmentLevelByNodeId.TryGetValue(nodeId, out var level) ? level : null;

        public int? GetCategoryCode(int nodeId) =>
            _categoryCodeByNodeId.TryGetValue(nodeId, out var category) ? category : null;
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
    private const int KnownCategoryCode = 27; // ORE, per HarvestableCategory

    private static FakeHarvestableNodeTracker KnownOreNodeTracker(int tier, int enchantmentLevel = 0) =>
        new(
            tierByNodeId: new Dictionary<int, int> { [NodeId] = tier },
            enchantmentLevelByNodeId: new Dictionary<int, int> { [NodeId] = enchantmentLevel },
            categoryCodeByNodeId: new Dictionary<int, int> { [NodeId] = KnownCategoryCode });

    private static PhotonEvent HarvestFinished(int actorEntityId, int amount, int nodeId = NodeId) =>
        new(1, new Dictionary<byte, object?> { [0] = actorEntityId, [3] = nodeId, [5] = amount, [252] = (byte)61 });

    private static PhotonEvent HarvestFinishedWithBonus(int actorEntityId, int baseAmount, int bonusAmount, int nodeId = NodeId) =>
        new(1, new Dictionary<byte, object?> { [0] = actorEntityId, [3] = nodeId, [5] = baseAmount, [6] = bonusAmount, [252] = (byte)61 });

    // Photon omits parameters at their default value - an interrupted swing (real yield 0) sends
    // no amount parameter at all, confirmed via live capture on 2026-08-02 (see class header).
    private static PhotonEvent HarvestFinishedWithNoYield(int actorEntityId, int nodeId = NodeId) =>
        new(1, new Dictionary<byte, object?> { [0] = actorEntityId, [3] = nodeId, [252] = (byte)61 });

    private static PhotonEvent UpdateFame(int actorEntityId, int fameDelta) =>
        new(1, new Dictionary<byte, object?> { [0] = actorEntityId, [2] = fameDelta, [252] = (byte)82 });

    [Fact]
    public async Task HarvestFinishedEvent_WithKnownTierAndCategory_AddsFullyResolvedItemIdWithRealAmount()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var (service, context) = CreateServiceWithOpenSession(connection);
        await service.StartSessionAsync("4213");
        var parser = new FakePhotonParser();
        var localPlayer = new FakeLocalPlayerTracker { CurrentEntityId = LocalPlayerEntityId };
        var nodeTracker = KnownOreNodeTracker(tier: 4);
        var router = new GatheringEventRouter(parser, service, localPlayer, nodeTracker);

        // Confirmed via a controlled live-capture experiment on 2026-08-02: the player watched
        // their exact resource count change by +2 across three swings, and HarvestFinished's
        // parameter 5 read 2 for every one of them - not the old hardcoded +1 approximation.
        await router.HandleEventAsync(HarvestFinished(actorEntityId: LocalPlayerEntityId, amount: 2));

        var item = Assert.Single(context.GatheredItems);
        Assert.Equal("T4_ORE", item.ItemId);
        Assert.Equal(2, item.Amount);
    }

    [Fact]
    public async Task HarvestFinishedEvent_WithNoYieldParameter_IsIgnored()
    {
        // Regression: this is exactly the bug the player caught - HarvestStart used to record +1
        // even when a swing was interrupted mid-animation and nothing landed in the inventory.
        // Confirmed via a controlled live-capture experiment (interrupted the third of three
        // swings, real inventory delta +2/+2/+0): the third HarvestFinished had no amount
        // parameter at all, matching the real zero gain.
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var (service, context) = CreateServiceWithOpenSession(connection);
        await service.StartSessionAsync("4213");
        var parser = new FakePhotonParser();
        var localPlayer = new FakeLocalPlayerTracker { CurrentEntityId = LocalPlayerEntityId };
        var nodeTracker = KnownOreNodeTracker(tier: 4);
        var router = new GatheringEventRouter(parser, service, localPlayer, nodeTracker);

        await router.HandleEventAsync(HarvestFinishedWithNoYield(actorEntityId: LocalPlayerEntityId));

        Assert.Empty(context.GatheredItems);
    }

    [Fact]
    public async Task HarvestFinishedEvent_WithGatheringSpecializationBonus_AddsBaseAmountPlusBonus()
    {
        // Confirmed via a controlled live-capture experiment on 2026-08-02: the player watched
        // their exact resource count change by +2, +2, +4 across three swings on one node - the
        // first two HarvestFinished events had no bonus parameter (equivalent to 0), the bonus
        // swing had parameter 5 = 2 *and* parameter 6 = 2 (2+2=4, matching the real gain exactly).
        // The bonus rides in a separate parameter, not folded into parameter 5.
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var (service, context) = CreateServiceWithOpenSession(connection);
        await service.StartSessionAsync("4213");
        var parser = new FakePhotonParser();
        var localPlayer = new FakeLocalPlayerTracker { CurrentEntityId = LocalPlayerEntityId };
        var nodeTracker = KnownOreNodeTracker(tier: 4, enchantmentLevel: 1);
        var router = new GatheringEventRouter(parser, service, localPlayer, nodeTracker);

        await router.HandleEventAsync(HarvestFinishedWithBonus(actorEntityId: LocalPlayerEntityId, baseAmount: 2, bonusAmount: 2));

        var item = Assert.Single(context.GatheredItems);
        Assert.Equal(4, item.Amount);
    }

    [Fact]
    public async Task HarvestFinishedEvent_WithZeroOrNegativeAmount_IsIgnored()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var (service, context) = CreateServiceWithOpenSession(connection);
        await service.StartSessionAsync("4213");
        var parser = new FakePhotonParser();
        var localPlayer = new FakeLocalPlayerTracker { CurrentEntityId = LocalPlayerEntityId };
        var nodeTracker = KnownOreNodeTracker(tier: 4);
        var router = new GatheringEventRouter(parser, service, localPlayer, nodeTracker);

        await router.HandleEventAsync(HarvestFinished(actorEntityId: LocalPlayerEntityId, amount: 0));

        Assert.Empty(context.GatheredItems);
    }

    [Fact]
    public async Task HarvestFinishedEvent_WithEnchantedNode_AddsLevelSuffixedItemId()
    {
        // Confirmed via live capture on 2026-08-02, cross-referenced against the player's own
        // manually-tallied gathering: matches ao-bin-dumps items.json's real UniqueName for
        // enchanted resources (e.g. "T5_ORE_LEVEL2@2" = Rare Titanium Ore).
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var (service, context) = CreateServiceWithOpenSession(connection);
        await service.StartSessionAsync("4213");
        var parser = new FakePhotonParser();
        var localPlayer = new FakeLocalPlayerTracker { CurrentEntityId = LocalPlayerEntityId };
        var nodeTracker = KnownOreNodeTracker(tier: 5, enchantmentLevel: 2);
        var router = new GatheringEventRouter(parser, service, localPlayer, nodeTracker);

        await router.HandleEventAsync(HarvestFinished(actorEntityId: LocalPlayerEntityId, amount: 2));

        var item = Assert.Single(context.GatheredItems);
        Assert.Equal("T5_ORE_LEVEL2@2", item.ItemId);
    }

    [Fact]
    public async Task HarvestFinishedEvent_WithUnenchantedNode_AddsBareItemIdWithNoLevelSuffix()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var (service, context) = CreateServiceWithOpenSession(connection);
        await service.StartSessionAsync("4213");
        var parser = new FakePhotonParser();
        var localPlayer = new FakeLocalPlayerTracker { CurrentEntityId = LocalPlayerEntityId };
        var nodeTracker = KnownOreNodeTracker(tier: 4, enchantmentLevel: 0);
        var router = new GatheringEventRouter(parser, service, localPlayer, nodeTracker);

        await router.HandleEventAsync(HarvestFinished(actorEntityId: LocalPlayerEntityId, amount: 1));

        var item = Assert.Single(context.GatheredItems);
        Assert.Equal("T4_ORE", item.ItemId);
    }

    [Fact]
    public async Task HarvestFinishedEvent_WithUnknownNode_FallsBackToBareNodeId()
    {
        // Regression: unlike HarvestStart, HarvestFinished carries no category code at all - if
        // the node's spawn broadcast was never captured (so category/tier are both unresolved),
        // the only identifying information left is the node id itself. The fallback should still
        // record *something* rather than dropping the swing.
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var (service, context) = CreateServiceWithOpenSession(connection);
        await service.StartSessionAsync("4213");
        var parser = new FakePhotonParser();
        var localPlayer = new FakeLocalPlayerTracker { CurrentEntityId = LocalPlayerEntityId };
        var nodeTracker = new FakeHarvestableNodeTracker(); // nothing known about this node
        var router = new GatheringEventRouter(parser, service, localPlayer, nodeTracker);

        await router.HandleEventAsync(HarvestFinished(actorEntityId: LocalPlayerEntityId, amount: 1));

        var item = Assert.Single(context.GatheredItems);
        Assert.Equal(NodeId.ToString(), item.ItemId);
    }

    [Fact]
    public async Task HarvestFinishedEvent_ByAnotherNearbyPlayer_IsIgnored()
    {
        // Regression: HarvestFinished is broadcast to everyone in the zone, not just the local
        // player (same as HarvestStart before it - a live capture showed two other players'
        // harvest swings recorded into the local player's own session before this filter existed).
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var (service, context) = CreateServiceWithOpenSession(connection);
        await service.StartSessionAsync("4213");
        var parser = new FakePhotonParser();
        var localPlayer = new FakeLocalPlayerTracker { CurrentEntityId = LocalPlayerEntityId };
        var router = new GatheringEventRouter(parser, service, localPlayer, new FakeHarvestableNodeTracker());

        await router.HandleEventAsync(HarvestFinished(actorEntityId: 448437, amount: 1));

        Assert.Empty(context.GatheredItems);
    }

    [Fact]
    public async Task HarvestFinishedEvent_WithUnknownLocalEntityId_IsIgnored()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var (service, context) = CreateServiceWithOpenSession(connection);
        await service.StartSessionAsync("4213");
        var parser = new FakePhotonParser();
        var localPlayer = new FakeLocalPlayerTracker { CurrentEntityId = null };
        var router = new GatheringEventRouter(parser, service, localPlayer, new FakeHarvestableNodeTracker());

        await router.HandleEventAsync(HarvestFinished(actorEntityId: LocalPlayerEntityId, amount: 1));

        Assert.Empty(context.GatheredItems);
    }

    [Fact]
    public async Task RepeatedHarvestFinishedEvents_AccumulateSeparateGatheredItemEntries()
    {
        // A resource node spans many swings before depleting - every finished swing must record
        // its own entry, not just a single tally at the end.
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var (service, context) = CreateServiceWithOpenSession(connection);
        await service.StartSessionAsync("4213");
        var parser = new FakePhotonParser();
        var localPlayer = new FakeLocalPlayerTracker { CurrentEntityId = LocalPlayerEntityId };
        var nodeTracker = KnownOreNodeTracker(tier: 4);
        var router = new GatheringEventRouter(parser, service, localPlayer, nodeTracker);

        for (var i = 0; i < 3; i++)
        {
            await router.HandleEventAsync(HarvestFinished(actorEntityId: LocalPlayerEntityId, amount: 2));
        }

        Assert.Equal(3, context.GatheredItems.Count());
        Assert.Equal(6, context.GatheredItems.Sum(item => item.Amount));
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
    public async Task HarvestFinishedEvent_WithNoActiveSession_IsIgnored()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var (service, context) = CreateServiceWithOpenSession(connection);
        var parser = new FakePhotonParser();
        var localPlayer = new FakeLocalPlayerTracker { CurrentEntityId = LocalPlayerEntityId };
        var router = new GatheringEventRouter(parser, service, localPlayer, new FakeHarvestableNodeTracker());

        await router.HandleEventAsync(HarvestFinished(actorEntityId: LocalPlayerEntityId, amount: 1));

        Assert.Empty(context.GatheredItems);
    }

    [Fact]
    public async Task UpdateFameEvent_ForLocalPlayer_AddsScaledFameLog()
    {
        // Wire-value scale confirmed via live capture on 2026-07-18: successive UpdateFame events'
        // running-total parameter (1) advanced by exactly the delta parameter (2) each time, and
        // dividing that delta by 10000 lines up with a plausible per-swing fame number (600000 -> 60).
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var (service, context) = CreateServiceWithOpenSession(connection);
        await service.StartSessionAsync("4213");
        var parser = new FakePhotonParser();
        var localPlayer = new FakeLocalPlayerTracker { CurrentEntityId = LocalPlayerEntityId };
        var router = new GatheringEventRouter(parser, service, localPlayer, new FakeHarvestableNodeTracker());

        await router.HandleEventAsync(UpdateFame(actorEntityId: LocalPlayerEntityId, fameDelta: 600000));

        var fameLog = Assert.Single(context.FameLogs);
        Assert.Equal("Gathering", fameLog.FameType);
        Assert.Equal(60, fameLog.Amount);
    }

    [Fact]
    public async Task UpdateFameEvent_ByAnotherNearbyPlayer_IsIgnored()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var (service, context) = CreateServiceWithOpenSession(connection);
        await service.StartSessionAsync("4213");
        var parser = new FakePhotonParser();
        var localPlayer = new FakeLocalPlayerTracker { CurrentEntityId = LocalPlayerEntityId };
        var router = new GatheringEventRouter(parser, service, localPlayer, new FakeHarvestableNodeTracker());

        await router.HandleEventAsync(UpdateFame(actorEntityId: 448437, fameDelta: 600000));

        Assert.Empty(context.FameLogs);
    }
}
