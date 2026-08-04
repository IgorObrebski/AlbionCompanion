using AlbionCompanion.Core.Data;
using AlbionCompanion.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AlbionCompanion.Gathering.Tests;

public class GatheringSessionServiceTests
{
    private static AppDbContext CreateInMemoryContext(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        var context = new AppDbContext(options);
        context.Database.EnsureCreated();
        return context;
    }

    private sealed class FakeLocalPlayerTracker : ILocalPlayerTracker
    {
        public int? CurrentEntityId { get; set; }
        public string? CurrentCharacterName { get; set; }
        public event EventHandler<Exception>? OnError;
    }

    private sealed class FakeCharacterService : ICharacterService
    {
        private readonly List<Character> _characters = new();

        public event EventHandler? CharactersChanged;

        public Task<IReadOnlyList<Character>> GetAllAsync() => Task.FromResult<IReadOnlyList<Character>>(_characters);

        public Task<Character> AddAsync(string name)
        {
            var character = new Character { Name = name, CreatedAt = DateTime.UtcNow };
            _characters.Add(character);
            return Task.FromResult(character);
        }

        public Task DeleteAsync(Guid id) => throw new NotImplementedException();
        public Task RenameAsync(Guid id, string newName) => throw new NotImplementedException();
        public Task<IReadOnlyList<CharacterOverview>> GetAllOverviewsAsync() => throw new NotImplementedException();
        public Task<CharacterOverview?> GetOverviewAsync(Guid characterId) => throw new NotImplementedException();
        public void NotifyCharactersChanged() => CharactersChanged?.Invoke(this, EventArgs.Empty);
    }

    private static GatheringSessionService CreateService(
        AppDbContext context, ILocalPlayerTracker? localPlayerTracker = null, ICharacterService? characterService = null) =>
        new(context, localPlayerTracker ?? new FakeLocalPlayerTracker(), characterService ?? new FakeCharacterService());

    [Fact]
    public async Task StartSessionAsync_CreatesOpenSession()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        using var context = CreateInMemoryContext(connection);
        var service = CreateService(context);

        await service.StartSessionAsync("Martlock");

        var active = await service.GetActiveSessionAsync();
        Assert.NotNull(active);
        Assert.Equal("Martlock", active!.StartLocation);
        Assert.Null(active.EndTime);
    }

    [Fact]
    public async Task StartSessionAsync_WhenAlreadyActive_DoesNotCreateSecondSession()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        using var context = CreateInMemoryContext(connection);
        var service = CreateService(context);

        await service.StartSessionAsync("Martlock");
        await service.StartSessionAsync("Bridgewatch");

        Assert.Single(context.GatheringSessions);
        var active = await service.GetActiveSessionAsync();
        Assert.Equal("Martlock", active!.StartLocation);
    }

    [Fact]
    public async Task StartSessionAsync_WhenAlreadyActive_UpdatesCurrentLocationButNotStartLocation()
    {
        // Regression: a wilderness session can roam through many open-world zones without
        // ending (only a return to a city/safe area ends it) - confirmed via live capture on
        // 2026-07-18 that a session which started in one zone and moved to another still showed
        // the first zone as "current" indefinitely, because StartSessionAsync used to no-op
        // entirely on an already-active session.
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        using var context = CreateInMemoryContext(connection);
        var service = CreateService(context);

        await service.StartSessionAsync("Martlock");
        await service.StartSessionAsync("Bridgewatch");

        var active = await service.GetActiveSessionAsync();
        Assert.Equal("Martlock", active!.StartLocation);
        Assert.Equal("Bridgewatch", active.CurrentLocation);
    }

    [Fact]
    public async Task StartSessionAsync_SetsCurrentLocationToStartLocationInitially()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        using var context = CreateInMemoryContext(connection);
        var service = CreateService(context);

        await service.StartSessionAsync("Martlock");

        var active = await service.GetActiveSessionAsync();
        Assert.Equal("Martlock", active!.CurrentLocation);
    }

    [Fact]
    public async Task EndSessionAsync_WithGatheredItems_ClosesSessionInstadOfDeletingIt()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        using var context = CreateInMemoryContext(connection);
        var service = CreateService(context);
        await service.StartSessionAsync("Martlock");
        await service.AddItemAsync("T4_ORE", 5);

        await service.EndSessionAsync();

        var session = Assert.Single(context.GatheringSessions);
        Assert.NotNull(session.EndTime);
    }

    [Fact]
    public async Task EndSessionAsync_WithNoActivity_DeletesTheEmptySession()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        using var context = CreateInMemoryContext(connection);
        var service = CreateService(context);
        await service.StartSessionAsync("Martlock");

        await service.EndSessionAsync();

        Assert.Empty(context.GatheringSessions);
    }

    [Fact]
    public async Task EndSessionAsync_WhenNoActiveSession_IsNoOp()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        using var context = CreateInMemoryContext(connection);
        var service = CreateService(context);

        await service.EndSessionAsync();

        Assert.Empty(context.GatheringSessions);
    }

    [Fact]
    public async Task AddItemAsync_WithNoActiveSession_IsIgnored()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        using var context = CreateInMemoryContext(connection);
        var service = CreateService(context);

        await service.AddItemAsync("T4_ORE", 5);

        Assert.Empty(context.GatheredItems);
    }

    [Fact]
    public async Task AddFameAsync_AccumulatesOnActiveSession()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        using var context = CreateInMemoryContext(connection);
        var service = CreateService(context);
        await service.StartSessionAsync("Martlock");

        await service.AddFameAsync("Gathering", 300);
        await service.AddFameAsync("Gathering", 600);

        var active = await service.GetActiveSessionAsync();
        Assert.Equal(900, active!.TotalFameEarned);
        Assert.Equal(2, context.FameLogs.Count());
    }

    [Fact]
    public async Task StartSessionAsync_CreatesOpenSession_RaisesOnSessionStarted()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        using var context = CreateInMemoryContext(connection);
        var service = CreateService(context);
        GatheringSession? raised = null;
        service.OnSessionStarted += (_, session) => raised = session;

        await service.StartSessionAsync("Martlock");

        Assert.NotNull(raised);
        Assert.Equal("Martlock", raised!.StartLocation);
    }

    [Fact]
    public async Task StartSessionAsync_WhenAlreadyActive_DoesNotRaiseOnSessionStarted()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        using var context = CreateInMemoryContext(connection);
        var service = CreateService(context);
        await service.StartSessionAsync("Martlock");
        var raiseCount = 0;
        service.OnSessionStarted += (_, _) => raiseCount++;

        await service.StartSessionAsync("Bridgewatch");

        Assert.Equal(0, raiseCount);
    }

    [Fact]
    public async Task EndSessionAsync_WithGatheredItems_RaisesOnSessionEnded()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        using var context = CreateInMemoryContext(connection);
        var service = CreateService(context);
        await service.StartSessionAsync("Martlock");
        await service.AddItemAsync("T4_ORE", 5);
        GatheringSession? raised = null;
        service.OnSessionEnded += (_, session) => raised = session;

        await service.EndSessionAsync();

        Assert.NotNull(raised);
        Assert.NotNull(raised!.EndTime);
    }

    [Fact]
    public async Task EndSessionAsync_WithNoActivity_DoesNotRaiseOnSessionEnded()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        using var context = CreateInMemoryContext(connection);
        var service = CreateService(context);
        await service.StartSessionAsync("Martlock");
        var raiseCount = 0;
        service.OnSessionEnded += (_, _) => raiseCount++;

        await service.EndSessionAsync();

        Assert.Equal(0, raiseCount);
    }

    [Fact]
    public async Task EndSessionAsync_WhenNoActiveSession_DoesNotRaiseOnSessionEnded()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        using var context = CreateInMemoryContext(connection);
        var service = CreateService(context);
        var raiseCount = 0;
        service.OnSessionEnded += (_, _) => raiseCount++;

        await service.EndSessionAsync();

        Assert.Equal(0, raiseCount);
    }

    [Fact]
    public async Task AddItemAsync_WithActiveSession_RaisesOnItemAdded()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        using var context = CreateInMemoryContext(connection);
        var service = CreateService(context);
        await service.StartSessionAsync("Martlock");
        GatheredItem? raised = null;
        service.OnItemAdded += (_, item) => raised = item;

        await service.AddItemAsync("T4_ORE", 5);

        Assert.NotNull(raised);
        Assert.Equal("T4_ORE", raised!.ItemId);
        Assert.Equal(5, raised.Amount);
    }

    [Fact]
    public async Task AddItemAsync_WithNoActiveSession_DoesNotRaiseOnItemAdded()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        using var context = CreateInMemoryContext(connection);
        var service = CreateService(context);
        var raiseCount = 0;
        service.OnItemAdded += (_, _) => raiseCount++;

        await service.AddItemAsync("T4_ORE", 5);

        Assert.Equal(0, raiseCount);
    }

    [Fact]
    public async Task AddFameAsync_WithActiveSession_RaisesOnFameAdded()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        using var context = CreateInMemoryContext(connection);
        var service = CreateService(context);
        await service.StartSessionAsync("Martlock");
        FameLog? raised = null;
        service.OnFameAdded += (_, fameLog) => raised = fameLog;

        await service.AddFameAsync("Gathering", 300);

        Assert.NotNull(raised);
        Assert.Equal("Gathering", raised!.FameType);
        Assert.Equal(300, raised.Amount);
    }

    [Fact]
    public async Task AddFameAsync_WithNoActiveSession_DoesNotRaiseOnFameAdded()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        using var context = CreateInMemoryContext(connection);
        var service = CreateService(context);
        var raiseCount = 0;
        service.OnFameAdded += (_, _) => raiseCount++;

        await service.AddFameAsync("Gathering", 300);

        Assert.Equal(0, raiseCount);
    }

    [Fact]
    public async Task StartSessionAsync_WithKnownCharacterName_ResolvesCharacterId()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        using var context = CreateInMemoryContext(connection);
        var characterService = new FakeCharacterService();
        var character = await characterService.AddAsync("Ejnsztain");
        // Also save to the database context so the foreign key constraint is satisfied
        context.Characters.Add(character);
        await context.SaveChangesAsync();
        var localPlayerTracker = new FakeLocalPlayerTracker { CurrentCharacterName = "Ejnsztain" };
        var service = CreateService(context, localPlayerTracker, characterService);

        await service.StartSessionAsync("Martlock");

        var active = await service.GetActiveSessionAsync();
        Assert.Equal(character.Id, active!.CharacterId);
    }

    [Fact]
    public async Task StartSessionAsync_WithUnregisteredCharacterName_LeavesCharacterIdNull()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        using var context = CreateInMemoryContext(connection);
        var localPlayerTracker = new FakeLocalPlayerTracker { CurrentCharacterName = "SomeoneNotRegistered" };
        var service = CreateService(context, localPlayerTracker, new FakeCharacterService());

        await service.StartSessionAsync("Martlock");

        var active = await service.GetActiveSessionAsync();
        Assert.Null(active!.CharacterId);
    }

    [Fact]
    public async Task StartSessionAsync_WithNoKnownCharacterName_LeavesCharacterIdNull()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        using var context = CreateInMemoryContext(connection);
        var service = CreateService(context);

        await service.StartSessionAsync("Martlock");

        var active = await service.GetActiveSessionAsync();
        Assert.Null(active!.CharacterId);
    }

    // Regression for a real orphaned-session bug found 2026-08-04: closing the game/app while
    // still in open world (no return-to-city zone transition) leaves EndTime null forever -
    // ZoneTracker only ever ends a session on that specific transition. Without this check, the
    // *next* app launch silently "resumes" that ancient session (GetActiveSessionAsync just
    // checks EndTime == null, with no concept of staleness), which never fires OnSessionStarted
    // (so no toast) and never re-resolves CharacterId (frozen at whatever it was, possibly null).

    [Fact]
    public async Task GetActiveSessionAsync_WithRecentActivity_StaysActive()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        using var context = CreateInMemoryContext(connection);
        var service = CreateService(context);
        await service.StartSessionAsync("Martlock");
        await service.AddItemAsync("T4_ORE", 5);

        var active = await service.GetActiveSessionAsync();

        Assert.NotNull(active);
        Assert.Null(active!.EndTime);
    }

    [Fact]
    public async Task GetActiveSessionAsync_InactiveTooLongWithActivity_ClosesItAndReturnsNull()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        using var context = CreateInMemoryContext(connection);
        var service = CreateService(context);
        await service.StartSessionAsync("Martlock");
        await service.AddItemAsync("T4_ORE", 5);

        var staleTime = DateTime.UtcNow.AddHours(-2);
        var session = await context.GatheringSessions.SingleAsync();
        session.StartTime = staleTime;
        await context.SaveChangesAsync();
        await context.GatheredItems.Where(i => i.SessionId == session.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(i => i.Timestamp, staleTime));

        var active = await service.GetActiveSessionAsync();

        Assert.Null(active);
        var persisted = Assert.Single(context.GatheringSessions);
        Assert.NotNull(persisted.EndTime);
    }

    [Fact]
    public async Task GetActiveSessionAsync_InactiveTooLongWithNoActivity_DeletesIt()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        using var context = CreateInMemoryContext(connection);
        var service = CreateService(context);
        await service.StartSessionAsync("Martlock");
        var session = await context.GatheringSessions.SingleAsync();
        session.StartTime = DateTime.UtcNow.AddHours(-2);
        await context.SaveChangesAsync();

        var active = await service.GetActiveSessionAsync();

        Assert.Null(active);
        Assert.Empty(context.GatheringSessions);
    }

    [Fact]
    public async Task StartSessionAsync_WhenActiveSessionIsStale_ClosesItAndStartsANewOne()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        using var context = CreateInMemoryContext(connection);
        var service = CreateService(context);
        await service.StartSessionAsync("Martlock");
        await service.AddItemAsync("T4_ORE", 5);
        var staleTime = DateTime.UtcNow.AddHours(-2);
        var staleSession = await context.GatheringSessions.SingleAsync();
        staleSession.StartTime = staleTime;
        await context.SaveChangesAsync();
        await context.GatheredItems.Where(i => i.SessionId == staleSession.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(i => i.Timestamp, staleTime));

        await service.StartSessionAsync("Bridgewatch");

        Assert.Equal(2, context.GatheringSessions.Count());
        var newSession = await service.GetActiveSessionAsync();
        Assert.Equal("Bridgewatch", newSession!.StartLocation);
        var oldSession = await context.GatheringSessions.SingleAsync(s => s.Id == staleSession.Id);
        Assert.NotNull(oldSession.EndTime);
    }
}
