using AlbionCompanion.Core.Data;
using AlbionCompanion.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AlbionCompanion.Gathering.Tests;

public class SessionHistoryServiceTests
{
    private sealed class SingleConnectionDbContextFactory : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options;

        public SingleConnectionDbContextFactory(SqliteConnection connection)
        {
            _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        }

        public AppDbContext CreateDbContext() => new(_options);

        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }

    private static (SessionHistoryService Service, AppDbContext Context) CreateService(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        var context = new AppDbContext(options);
        context.Database.EnsureCreated();
        var service = new SessionHistoryService(new SingleConnectionDbContextFactory(connection));
        return (service, context);
    }

    [Fact]
    public async Task GetCompletedSessionsAsync_NoSessions_ReturnsEmpty()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var (service, _) = CreateService(connection);

        var result = await service.GetCompletedSessionsAsync();

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetCompletedSessionsAsync_ExcludesActiveSessions()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var (service, context) = CreateService(connection);
        context.GatheringSessions.Add(new GatheringSession { StartTime = DateTime.UtcNow, StartLocation = "Martlock", EndTime = null });
        context.GatheringSessions.Add(new GatheringSession { StartTime = DateTime.UtcNow, StartLocation = "Lymhurst", EndTime = DateTime.UtcNow });
        await context.SaveChangesAsync();

        var result = await service.GetCompletedSessionsAsync();

        Assert.Single(result);
        Assert.Equal("Lymhurst", result[0].StartLocation);
    }

    [Fact]
    public async Task GetCompletedSessionsAsync_OrdersByStartTimeDescending()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var (service, context) = CreateService(connection);
        var older = new GatheringSession { StartTime = new DateTime(2026, 1, 1), StartLocation = "Older", EndTime = new DateTime(2026, 1, 1, 1, 0, 0) };
        var newer = new GatheringSession { StartTime = new DateTime(2026, 1, 2), StartLocation = "Newer", EndTime = new DateTime(2026, 1, 2, 1, 0, 0) };
        context.GatheringSessions.AddRange(older, newer);
        await context.SaveChangesAsync();

        var result = await service.GetCompletedSessionsAsync();

        Assert.Equal("Newer", result[0].StartLocation);
        Assert.Equal("Older", result[1].StartLocation);
    }

    [Fact]
    public async Task GetCompletedSessionsAsync_TotalItemsCollected_SumsAllGatheredItems()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var (service, context) = CreateService(connection);
        var session = new GatheringSession { StartTime = DateTime.UtcNow, StartLocation = "Martlock", EndTime = DateTime.UtcNow, TotalFameEarned = 900 };
        context.GatheringSessions.Add(session);
        await context.SaveChangesAsync();
        context.GatheredItems.Add(new GatheredItem { SessionId = session.Id, ItemId = "T4_ORE", Amount = 5, Timestamp = DateTime.UtcNow });
        context.GatheredItems.Add(new GatheredItem { SessionId = session.Id, ItemId = "T4_WOOD", Amount = 3, Timestamp = DateTime.UtcNow });
        await context.SaveChangesAsync();

        var result = await service.GetCompletedSessionsAsync();

        Assert.Equal(8, result[0].TotalItemsCollected);
        Assert.Equal(900, result[0].TotalFameEarned);
    }

    [Fact]
    public async Task GetSessionDetailAsync_ExistingCompletedSession_ReturnsAggregatedItemTotals()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var (service, context) = CreateService(connection);
        var session = new GatheringSession { StartTime = DateTime.UtcNow, StartLocation = "Martlock", EndTime = DateTime.UtcNow, TotalFameEarned = 300 };
        context.GatheringSessions.Add(session);
        await context.SaveChangesAsync();
        context.GatheredItems.Add(new GatheredItem { SessionId = session.Id, ItemId = "T4_ORE", Amount = 5, Timestamp = DateTime.UtcNow });
        context.GatheredItems.Add(new GatheredItem { SessionId = session.Id, ItemId = "T4_ORE", Amount = 2, Timestamp = DateTime.UtcNow });
        await context.SaveChangesAsync();

        var result = await service.GetSessionDetailAsync(session.Id);

        Assert.NotNull(result);
        Assert.Equal(7, result!.ItemTotals["T4_ORE"]);
        Assert.Equal(300, result.TotalFameEarned);
        Assert.Equal("Martlock", result.StartLocation);
    }

    [Fact]
    public async Task GetSessionDetailAsync_NonexistentSession_ReturnsNull()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var (service, _) = CreateService(connection);

        var result = await service.GetSessionDetailAsync(Guid.NewGuid());

        Assert.Null(result);
    }

    [Fact]
    public async Task GetSessionDetailAsync_ActiveSession_ReturnsNull()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var (service, context) = CreateService(connection);
        var session = new GatheringSession { StartTime = DateTime.UtcNow, StartLocation = "Martlock", EndTime = null };
        context.GatheringSessions.Add(session);
        await context.SaveChangesAsync();

        var result = await service.GetSessionDetailAsync(session.Id);

        Assert.Null(result);
    }
}
