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

    private static (GatheringSessionService Service, AppDbContext Context) CreateServiceWithOpenSession(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        var context = new AppDbContext(options);
        context.Database.EnsureCreated();
        var service = new GatheringSessionService(context);
        return (service, context);
    }

    [Fact]
    public async Task HarvestStartEvent_AddsOneUnitOfTheHarvestedItemToActiveSession()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var (service, context) = CreateServiceWithOpenSession(connection);
        await service.StartSessionAsync("4213");
        var parser = new FakePhotonParser();
        var router = new GatheringEventRouter(parser, service);

        // code=59 (HarvestStart), item type id=27 - layout confirmed against live captures.
        // Fires on every swing regardless of whether the node gets fully depleted, unlike
        // HarvestFinished (61) which only fires on full depletion to zero charges.
        await router.HandleEventAsync(new PhotonEvent(1,
            new Dictionary<byte, object?> { [4] = 27, [252] = (byte)59 }));

        var item = Assert.Single(context.GatheredItems);
        Assert.Equal("27", item.ItemId);
        Assert.Equal(1, item.Amount);
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
        var router = new GatheringEventRouter(parser, service);

        for (var i = 0; i < 3; i++)
        {
            await router.HandleEventAsync(new PhotonEvent(1,
                new Dictionary<byte, object?> { [4] = 27, [252] = (byte)59 }));
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
        var router = new GatheringEventRouter(parser, service);

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
        var router = new GatheringEventRouter(parser, service);

        await router.HandleEventAsync(new PhotonEvent(1,
            new Dictionary<byte, object?> { [4] = 27, [252] = (byte)59 }));

        Assert.Empty(context.GatheredItems);
    }
}
