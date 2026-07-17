using AlbionCompanion.Core.Data;
using AlbionCompanion.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AlbionCompanion.Core.Tests.Data;

public class AppDbContextTests
{
    private static AppDbContext CreateInMemoryContext(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        var context = new AppDbContext(options);
        context.Database.EnsureCreated();
        return context;
    }

    [Fact]
    public void PriceCache_EnforcesCompositeKeyUniqueness()
    {
        // Two separate DbContext instances (sharing one open connection so the
        // in-memory Sqlite database survives between them) simulate two independent
        // save operations, so the second insert actually reaches the database instead
        // of being rejected by the first context's local change tracker.
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        using (var firstContext = CreateInMemoryContext(connection))
        {
            firstContext.PriceCaches.Add(new PriceCache
            {
                ItemId = "T4_ORE",
                Location = "Lymhurst",
                SellPriceMin = 100,
                BuyPriceMax = 90,
                LastUpdated = DateTime.UtcNow
            });
            firstContext.SaveChanges();
        }

        using var secondContext = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        secondContext.PriceCaches.Add(new PriceCache
        {
            ItemId = "T4_ORE",
            Location = "Lymhurst",
            SellPriceMin = 999,
            BuyPriceMax = 999,
            LastUpdated = DateTime.UtcNow
        });

        Assert.Throws<DbUpdateException>(() => secondContext.SaveChanges());
    }

    [Fact]
    public void AllDbSets_AreReachableAndPersistRoundTrip()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        using var context = CreateInMemoryContext(connection);

        var session = new GatheringSession
        {
            StartTime = DateTime.UtcNow,
            StartLocation = "Lymhurst",
            TotalFameEarned = 0
        };
        context.GatheringSessions.Add(session);
        context.SaveChanges();

        Assert.Single(context.GatheringSessions);
    }

    [Fact]
    public void RawGatheringEvents_PersistRoundTrip_WithNullableSessionId()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        using var context = CreateInMemoryContext(connection);

        context.RawGatheringEvents.Add(new RawGatheringEvent
        {
            SessionId = null,
            PhotonCode = 1,
            SemanticEventCode = 59,
            ParametersJson = "{\"0\":535802,\"3\":2955,\"4\":27,\"252\":59}",
            Timestamp = DateTime.UtcNow
        });
        context.SaveChanges();

        var stored = Assert.Single(context.RawGatheringEvents);
        Assert.Null(stored.SessionId);
        Assert.Equal((byte)59, stored.SemanticEventCode);
    }

    [Fact]
    public void RawGatheringEvents_PersistWithSessionId()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        using var context = CreateInMemoryContext(connection);

        var session = new GatheringSession { StartTime = DateTime.UtcNow, StartLocation = "Lymhurst" };
        context.GatheringSessions.Add(session);
        context.SaveChanges();

        context.RawGatheringEvents.Add(new RawGatheringEvent
        {
            SessionId = session.Id,
            PhotonCode = 1,
            SemanticEventCode = null,
            ParametersJson = "{}",
            Timestamp = DateTime.UtcNow
        });
        context.SaveChanges();

        var stored = Assert.Single(context.RawGatheringEvents);
        Assert.Equal(session.Id, stored.SessionId);
        Assert.Null(stored.SemanticEventCode);
    }
}
