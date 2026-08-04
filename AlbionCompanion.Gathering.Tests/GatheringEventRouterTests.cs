using AlbionCompanion.Core.Data;
using AlbionCompanion.Core.Models;
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
        public string? CurrentCharacterName { get; set; }
        public event EventHandler<Exception>? OnError;
    }

    private sealed class FakeCharacterService : ICharacterService
    {
        public event EventHandler? CharactersChanged;
        public Task<IReadOnlyList<Character>> GetAllAsync() => Task.FromResult<IReadOnlyList<Character>>(Array.Empty<Character>());
        public Task<Character> AddAsync(string name) => throw new NotImplementedException();
        public Task DeleteAsync(Guid id) => throw new NotImplementedException();
        public Task RenameAsync(Guid id, string newName) => throw new NotImplementedException();
        public Task<IReadOnlyList<CharacterOverview>> GetAllOverviewsAsync() => throw new NotImplementedException();
        public Task<CharacterOverview?> GetOverviewAsync(Guid characterId) => throw new NotImplementedException();
        public void NotifyCharactersChanged() => CharactersChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed class FakeItemDictionaryService : IItemDictionaryService
    {
        private readonly Dictionary<int, ItemDictionary> _byIndex;

        public FakeItemDictionaryService(Dictionary<int, ItemDictionary>? byIndex = null) =>
            _byIndex = byIndex ?? new Dictionary<int, ItemDictionary>();

        public Task<IEnumerable<ItemDictionary>> SearchItemsAsync(string query) =>
            Task.FromResult(Enumerable.Empty<ItemDictionary>());

        public Task<ItemDictionary?> GetItemByIdAsync(string id) => Task.FromResult<ItemDictionary?>(null);

        public Task<ItemDictionary?> GetItemByIndexAsync(int index) =>
            Task.FromResult(_byIndex.TryGetValue(index, out var item) ? item : null);

        public Task<IReadOnlyDictionary<string, ItemDictionary>> GetItemsByIdAsync(IEnumerable<string> ids) =>
            Task.FromResult<IReadOnlyDictionary<string, ItemDictionary>>(new Dictionary<string, ItemDictionary>());

        public Task SeedFromJsonAsync() => Task.CompletedTask;
    }

    private static (GatheringSessionService Service, AppDbContext Context) CreateServiceWithOpenSession(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        var context = new AppDbContext(options);
        context.Database.EnsureCreated();
        var service = new GatheringSessionService(context, new FakeLocalPlayerTracker(), new FakeCharacterService());
        return (service, context);
    }

    private const int LocalPlayerEntityId = 535802;
    private const int KnownItemIndex = 978; // T5_ORE_LEVEL2@2, per live capture 2026-08-02

    private static FakeItemDictionaryService KnownItemDictionary(string uniqueName, int index = KnownItemIndex) =>
        new(new Dictionary<int, ItemDictionary> { [index] = new() { UniqueName = uniqueName, Index = index } });

    private static PhotonEvent HarvestFinished(int actorEntityId, int amount, int itemIndex = KnownItemIndex) =>
        new(1, new Dictionary<byte, object?> { [0] = actorEntityId, [4] = itemIndex, [5] = amount, [252] = (byte)61 });

    private static PhotonEvent HarvestFinishedWithBonus(int actorEntityId, int baseAmount, int bonusAmount, int itemIndex = KnownItemIndex) =>
        new(1, new Dictionary<byte, object?> { [0] = actorEntityId, [4] = itemIndex, [5] = baseAmount, [6] = bonusAmount, [252] = (byte)61 });

    // Photon omits parameters at their default value - an interrupted swing (real yield 0) sends
    // no amount parameter at all, confirmed via live capture on 2026-08-02 (see class header).
    private static PhotonEvent HarvestFinishedWithNoYield(int actorEntityId, int itemIndex = KnownItemIndex) =>
        new(1, new Dictionary<byte, object?> { [0] = actorEntityId, [4] = itemIndex, [252] = (byte)61 });

    private static PhotonEvent UpdateFame(int actorEntityId, int fameDelta) =>
        new(1, new Dictionary<byte, object?> { [0] = actorEntityId, [2] = fameDelta, [252] = (byte)82 });

    private static PhotonEvent UpdateMoney(int actorEntityId, long rawTotal) =>
        new(1, new Dictionary<byte, object?> { [0] = actorEntityId, [1] = rawTotal, [252] = (byte)81 });

    [Fact]
    public async Task HarvestFinishedEvent_WithKnownItemIndex_AddsResolvedItemIdWithRealAmount()
    {
        // Confirmed via live capture 2026-08-02: HarvestFinished's parameter 4 is exactly
        // ao-bin-dumps items.json's numeric "Index" for the harvested item - cross-referenced
        // several live swings (e.g. 978 -> "T5_ORE_LEVEL2@2") against real gameplay.
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var (service, context) = CreateServiceWithOpenSession(connection);
        await service.StartSessionAsync("4213");
        var parser = new FakePhotonParser();
        var localPlayer = new FakeLocalPlayerTracker { CurrentEntityId = LocalPlayerEntityId };
        var itemDictionary = KnownItemDictionary("T5_ORE_LEVEL2@2");
        var router = new GatheringEventRouter(parser, service, localPlayer, itemDictionary);

        // Confirmed via a controlled live-capture experiment on 2026-08-02: the player watched
        // their exact resource count change by +2 across three swings, and HarvestFinished's
        // parameter 5 read 2 for every one of them - not the old hardcoded +1 approximation.
        await router.HandleEventAsync(HarvestFinished(actorEntityId: LocalPlayerEntityId, amount: 2));

        var item = Assert.Single(context.GatheredItems);
        Assert.Equal("T5_ORE_LEVEL2@2", item.ItemId);
        Assert.Equal(2, item.Amount);
    }

    [Fact]
    public async Task HarvestFinishedEvent_WithUnknownItemIndex_FallsBackToBareIndex()
    {
        // Regression: an index not in the dictionary (e.g. seeding failed, or a genuinely
        // uncovered item type) should still record *something* rather than dropping the swing.
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var (service, context) = CreateServiceWithOpenSession(connection);
        await service.StartSessionAsync("4213");
        var parser = new FakePhotonParser();
        var localPlayer = new FakeLocalPlayerTracker { CurrentEntityId = LocalPlayerEntityId };
        var router = new GatheringEventRouter(parser, service, localPlayer, new FakeItemDictionaryService());

        await router.HandleEventAsync(HarvestFinished(actorEntityId: LocalPlayerEntityId, amount: 1, itemIndex: 999999));

        var item = Assert.Single(context.GatheredItems);
        Assert.Equal("999999", item.ItemId);
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
        var router = new GatheringEventRouter(parser, service, localPlayer, KnownItemDictionary("T4_ORE"));

        await router.HandleEventAsync(HarvestFinishedWithNoYield(actorEntityId: LocalPlayerEntityId));

        Assert.Empty(context.GatheredItems);
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
        var router = new GatheringEventRouter(parser, service, localPlayer, KnownItemDictionary("T4_ORE"));

        await router.HandleEventAsync(HarvestFinished(actorEntityId: LocalPlayerEntityId, amount: 0));

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
        var router = new GatheringEventRouter(parser, service, localPlayer, KnownItemDictionary("T4_ORE_LEVEL1@1"));

        await router.HandleEventAsync(HarvestFinishedWithBonus(actorEntityId: LocalPlayerEntityId, baseAmount: 2, bonusAmount: 2));

        var item = Assert.Single(context.GatheredItems);
        Assert.Equal(4, item.Amount);
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
        var router = new GatheringEventRouter(parser, service, localPlayer, new FakeItemDictionaryService());

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
        var router = new GatheringEventRouter(parser, service, localPlayer, new FakeItemDictionaryService());

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
        var router = new GatheringEventRouter(parser, service, localPlayer, KnownItemDictionary("T4_ORE"));

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
        var router = new GatheringEventRouter(parser, service, localPlayer, new FakeItemDictionaryService());

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
        var router = new GatheringEventRouter(parser, service, localPlayer, new FakeItemDictionaryService());

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
        var router = new GatheringEventRouter(parser, service, localPlayer, new FakeItemDictionaryService());

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
        var router = new GatheringEventRouter(parser, service, localPlayer, new FakeItemDictionaryService());

        await router.HandleEventAsync(UpdateFame(actorEntityId: 448437, fameDelta: 600000));

        Assert.Empty(context.FameLogs);
    }

    [Fact]
    public async Task UpdateMoneyEvent_FirstSighting_OnlySeedsBaselineWithoutRecordingAGain()
    {
        // Confirmed via live capture on 2026-08-03: unlike UpdateFame, UpdateMoney carries no
        // delta parameter, only a running total - the very first sighting can't have a delta
        // against nothing without wrongly recording the player's entire prior wealth as a gain.
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var (service, context) = CreateServiceWithOpenSession(connection);
        await service.StartSessionAsync("4213");
        var parser = new FakePhotonParser();
        var localPlayer = new FakeLocalPlayerTracker { CurrentEntityId = LocalPlayerEntityId };
        var router = new GatheringEventRouter(parser, service, localPlayer, new FakeItemDictionaryService());

        await router.HandleEventAsync(UpdateMoney(actorEntityId: LocalPlayerEntityId, rawTotal: 2504384241));

        Assert.Empty(context.SilverLogs);
    }

    [Fact]
    public async Task UpdateMoneyEvent_SecondSighting_RecordsScaledDeltaFromBaseline()
    {
        // Wire-value scale confirmed via live capture on 2026-08-03: two running-total samples
        // (2504384241 -> displayed 250438, then 2505306831 -> displayed 250530) matched the
        // player's own reported in-game silver gain of +92 once divided by the same 10000x scale
        // UpdateFame uses.
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var (service, context) = CreateServiceWithOpenSession(connection);
        await service.StartSessionAsync("4213");
        var parser = new FakePhotonParser();
        var localPlayer = new FakeLocalPlayerTracker { CurrentEntityId = LocalPlayerEntityId };
        var router = new GatheringEventRouter(parser, service, localPlayer, new FakeItemDictionaryService());

        await router.HandleEventAsync(UpdateMoney(actorEntityId: LocalPlayerEntityId, rawTotal: 2504384241));
        await router.HandleEventAsync(UpdateMoney(actorEntityId: LocalPlayerEntityId, rawTotal: 2505306831));

        var silverLog = Assert.Single(context.SilverLogs);
        Assert.Equal(92, silverLog.Amount);
    }

    [Fact]
    public async Task UpdateMoneyEvent_ByAnotherNearbyPlayer_IsIgnoredWithoutMovingBaseline()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var (service, context) = CreateServiceWithOpenSession(connection);
        await service.StartSessionAsync("4213");
        var parser = new FakePhotonParser();
        var localPlayer = new FakeLocalPlayerTracker { CurrentEntityId = LocalPlayerEntityId };
        var router = new GatheringEventRouter(parser, service, localPlayer, new FakeItemDictionaryService());

        await router.HandleEventAsync(UpdateMoney(actorEntityId: LocalPlayerEntityId, rawTotal: 2504384241));
        await router.HandleEventAsync(UpdateMoney(actorEntityId: 448437, rawTotal: 9999999999));
        await router.HandleEventAsync(UpdateMoney(actorEntityId: LocalPlayerEntityId, rawTotal: 2505306831));

        var silverLog = Assert.Single(context.SilverLogs);
        Assert.Equal(92, silverLog.Amount);
    }
}
