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

        var result = await service.GetCompletedSessionsAsync(new SessionQuery());

        Assert.Empty(result.Items);
        Assert.Equal(0, result.TotalCount);
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

        var result = await service.GetCompletedSessionsAsync(new SessionQuery());

        Assert.Single(result.Items);
        Assert.Equal("Lymhurst", result.Items[0].StartLocation);
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

        var result = await service.GetCompletedSessionsAsync(new SessionQuery());

        Assert.Equal("Newer", result.Items[0].StartLocation);
        Assert.Equal("Older", result.Items[1].StartLocation);
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

        var result = await service.GetCompletedSessionsAsync(new SessionQuery());

        Assert.Equal(8, result.Items[0].TotalItemsCollected);
        Assert.Equal(900, result.Items[0].TotalFameEarned);
    }

    [Fact]
    public async Task GetCompletedSessionsAsync_Pagination_ReturnsRequestedPageAndTotalCount()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var (service, context) = CreateService(connection);
        for (var i = 0; i < 5; i++)
        {
            context.GatheringSessions.Add(new GatheringSession
            {
                StartTime = new DateTime(2026, 1, 1).AddDays(i),
                StartLocation = $"Zone{i}",
                EndTime = new DateTime(2026, 1, 1).AddDays(i).AddHours(1),
            });
        }

        await context.SaveChangesAsync();

        var result = await service.GetCompletedSessionsAsync(new SessionQuery(Page: 2, PageSize: 2));

        Assert.Equal(5, result.TotalCount);
        Assert.Equal(3, result.TotalPages);
        Assert.Equal(2, result.Items.Count);
        // Default sort is StartTime descending, so page 2 (items 3-4 of 5) is Zone2 then Zone1.
        Assert.Equal("Zone2", result.Items[0].StartLocation);
        Assert.Equal("Zone1", result.Items[1].StartLocation);
    }

    [Fact]
    public async Task GetCompletedSessionsAsync_LocationFilter_MatchesSubstringCaseInsensitively()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var (service, context) = CreateService(connection);
        context.GatheringSessions.Add(new GatheringSession { StartTime = DateTime.UtcNow, StartLocation = "Cairn Camain", EndTime = DateTime.UtcNow });
        context.GatheringSessions.Add(new GatheringSession { StartTime = DateTime.UtcNow, StartLocation = "Martlock", EndTime = DateTime.UtcNow });
        await context.SaveChangesAsync();

        var result = await service.GetCompletedSessionsAsync(new SessionQuery(LocationFilter: "cairn"));

        Assert.Single(result.Items);
        Assert.Equal("Cairn Camain", result.Items[0].StartLocation);
    }

    [Fact]
    public async Task GetCompletedSessionsAsync_SortByFameAscending_OrdersLowestFirst()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var (service, context) = CreateService(connection);
        context.GatheringSessions.Add(new GatheringSession { StartTime = DateTime.UtcNow, StartLocation = "High", EndTime = DateTime.UtcNow, TotalFameEarned = 900 });
        context.GatheringSessions.Add(new GatheringSession { StartTime = DateTime.UtcNow, StartLocation = "Low", EndTime = DateTime.UtcNow, TotalFameEarned = 100 });
        await context.SaveChangesAsync();

        var result = await service.GetCompletedSessionsAsync(new SessionQuery(SortBy: SessionSortColumn.Fame, SortDescending: false));

        Assert.Equal("Low", result.Items[0].StartLocation);
        Assert.Equal("High", result.Items[1].StartLocation);
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
        var total = Assert.Single(result!.ItemTotals);
        Assert.Equal("T4_ORE", total.ItemId);
        Assert.Equal(7, total.Amount);
        Assert.Equal(300, result.TotalFameEarned);
        Assert.Equal("Martlock", result.StartLocation);
    }

    [Fact]
    public async Task GetSessionDetailAsync_SameItemDifferentLocations_TrackedSeparately()
    {
        // A session can roam through multiple zones without ending (see the 2026-08-02 roaming
        // fix) - the same item gathered in two locations must stay as two distinct totals.
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var (service, context) = CreateService(connection);
        var session = new GatheringSession { StartTime = DateTime.UtcNow, StartLocation = "Cairn Camain", EndTime = DateTime.UtcNow };
        context.GatheringSessions.Add(session);
        await context.SaveChangesAsync();
        context.GatheredItems.Add(new GatheredItem { SessionId = session.Id, ItemId = "T4_ORE", Amount = 5, Location = "Cairn Camain", Timestamp = DateTime.UtcNow });
        context.GatheredItems.Add(new GatheredItem { SessionId = session.Id, ItemId = "T4_ORE", Amount = 3, Location = "Mawar Gorge", Timestamp = DateTime.UtcNow });
        await context.SaveChangesAsync();

        var result = await service.GetSessionDetailAsync(session.Id);

        Assert.NotNull(result);
        Assert.Equal(2, result!.ItemTotals.Count);
        Assert.Equal(5, result.ItemTotals.Single(t => t.Location == "Cairn Camain").Amount);
        Assert.Equal(3, result.ItemTotals.Single(t => t.Location == "Mawar Gorge").Amount);
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

    [Fact]
    public async Task GetCompletedSessionsAsync_FilteredByCharacterId_ReturnsOnlyThatCharactersSessions()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var (service, context) = CreateService(connection);
        var characterA = Guid.NewGuid();
        var characterB = Guid.NewGuid();
        context.Characters.Add(new Character { Id = characterA, Name = "Ejnsztain", CreatedAt = DateTime.UtcNow });
        context.Characters.Add(new Character { Id = characterB, Name = "Valdekir", CreatedAt = DateTime.UtcNow });
        context.GatheringSessions.Add(new GatheringSession { StartTime = DateTime.UtcNow, StartLocation = "Martlock", EndTime = DateTime.UtcNow, CharacterId = characterA });
        context.GatheringSessions.Add(new GatheringSession { StartTime = DateTime.UtcNow, StartLocation = "Lymhurst", EndTime = DateTime.UtcNow, CharacterId = characterB });
        await context.SaveChangesAsync();

        var result = await service.GetCompletedSessionsAsync(new SessionQuery(CharacterId: characterA));

        Assert.Single(result.Items);
        Assert.Equal("Martlock", result.Items[0].StartLocation);
        Assert.Equal("Ejnsztain", result.Items[0].CharacterName);
    }

    [Fact]
    public async Task GetCompletedSessionsAsync_SessionWithNoCharacter_ProjectsUnknown()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var (service, context) = CreateService(connection);
        context.GatheringSessions.Add(new GatheringSession { StartTime = DateTime.UtcNow, StartLocation = "Martlock", EndTime = DateTime.UtcNow, CharacterId = null });
        await context.SaveChangesAsync();

        var result = await service.GetCompletedSessionsAsync(new SessionQuery());

        Assert.Equal("Unknown", result.Items[0].CharacterName);
    }
}
