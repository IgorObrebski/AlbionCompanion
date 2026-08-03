using AlbionCompanion.Core.Data;
using AlbionCompanion.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AlbionCompanion.Gathering.Tests;

public class CharacterServiceTests
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

    private static (CharacterService Service, AppDbContext Context) CreateService(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        var context = new AppDbContext(options);
        context.Database.EnsureCreated();
        return (new CharacterService(new SingleConnectionDbContextFactory(connection)), context);
    }

    [Fact]
    public async Task AddAsync_CreatesCharacter()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var (service, context) = CreateService(connection);

        var character = await service.AddAsync("Ejnsztain");

        Assert.Equal("Ejnsztain", character.Name);
        Assert.Single(context.Characters);
    }

    [Fact]
    public async Task AddAsync_DuplicateName_ThrowsDbUpdateException()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var (service, _) = CreateService(connection);
        await service.AddAsync("Ejnsztain");

        await Assert.ThrowsAsync<DbUpdateException>(() => service.AddAsync("Ejnsztain"));
    }

    [Fact]
    public async Task DeleteAsync_RemovesCharacterButDetachesItsSessionsInsteadOfDeletingThem()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var (service, context) = CreateService(connection);
        var character = await service.AddAsync("Ejnsztain");
        context.GatheringSessions.Add(new GatheringSession
        {
            StartTime = DateTime.UtcNow,
            StartLocation = "Martlock",
            CurrentLocation = "Martlock",
            CharacterId = character.Id,
        });
        await context.SaveChangesAsync();

        await service.DeleteAsync(character.Id);

        context.ChangeTracker.Clear();
        Assert.Empty(await service.GetAllAsync());
        var session = Assert.Single(context.GatheringSessions);
        Assert.Null(session.CharacterId);
    }

    [Fact]
    public async Task DeleteAsync_UnknownId_IsNoOp()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var (service, _) = CreateService(connection);

        await service.DeleteAsync(Guid.NewGuid());

        Assert.Empty(await service.GetAllAsync());
    }

    [Fact]
    public async Task GetOverviewAsync_AggregatesFameSilverItemsAndLastActive()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var (service, context) = CreateService(connection);
        var character = await service.AddAsync("Ejnsztain");
        var startTime = new DateTime(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc);
        var session = new GatheringSession
        {
            StartTime = startTime,
            EndTime = startTime.AddHours(1),
            StartLocation = "Martlock",
            CurrentLocation = "Martlock",
            CharacterId = character.Id,
            TotalFameEarned = 500,
            TotalSilverEarned = 92,
        };
        context.GatheringSessions.Add(session);
        context.GatheredItems.Add(new GatheredItem { SessionId = session.Id, ItemId = "T4_ORE", Amount = 10, Location = "Martlock", Timestamp = startTime });
        await context.SaveChangesAsync();

        var overview = await service.GetOverviewAsync(character.Id);

        Assert.NotNull(overview);
        Assert.Equal(500, overview!.TotalFameEarned);
        Assert.Equal(92, overview.TotalSilverEarned);
        Assert.Equal(10, overview.TotalItemsCollected);
        Assert.Equal(startTime, overview.LastActive);
        Assert.False(overview.HasActiveSession);
    }

    [Fact]
    public async Task GetOverviewAsync_WithOpenSession_HasActiveSessionIsTrue()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var (service, context) = CreateService(connection);
        var character = await service.AddAsync("Ejnsztain");
        context.GatheringSessions.Add(new GatheringSession
        {
            StartTime = DateTime.UtcNow,
            StartLocation = "Martlock",
            CurrentLocation = "Martlock",
            CharacterId = character.Id,
        });
        await context.SaveChangesAsync();

        var overview = await service.GetOverviewAsync(character.Id);

        Assert.True(overview!.HasActiveSession);
    }

    [Fact]
    public async Task GetOverviewAsync_UnknownCharacterId_ReturnsNull()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var (service, _) = CreateService(connection);

        var overview = await service.GetOverviewAsync(Guid.NewGuid());

        Assert.Null(overview);
    }

    [Fact]
    public async Task GetAllOverviewsAsync_ReturnsOneEntryPerCharacter()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var (service, _) = CreateService(connection);
        await service.AddAsync("Ejnsztain");
        await service.AddAsync("Valdekir");

        var overviews = await service.GetAllOverviewsAsync();

        Assert.Equal(2, overviews.Count);
    }
}
