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

    [Fact]
    public async Task StartSessionAsync_CreatesOpenSession()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        using var context = CreateInMemoryContext(connection);
        var service = new GatheringSessionService(context);

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
        var service = new GatheringSessionService(context);

        await service.StartSessionAsync("Martlock");
        await service.StartSessionAsync("Bridgewatch");

        Assert.Single(context.GatheringSessions);
        var active = await service.GetActiveSessionAsync();
        Assert.Equal("Martlock", active!.StartLocation);
    }

    [Fact]
    public async Task EndSessionAsync_WithGatheredItems_ClosesSessionInstadOfDeletingIt()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        using var context = CreateInMemoryContext(connection);
        var service = new GatheringSessionService(context);
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
        var service = new GatheringSessionService(context);
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
        var service = new GatheringSessionService(context);

        await service.EndSessionAsync();

        Assert.Empty(context.GatheringSessions);
    }

    [Fact]
    public async Task AddItemAsync_WithNoActiveSession_IsIgnored()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        using var context = CreateInMemoryContext(connection);
        var service = new GatheringSessionService(context);

        await service.AddItemAsync("T4_ORE", 5);

        Assert.Empty(context.GatheredItems);
    }

    [Fact]
    public async Task AddFameAsync_AccumulatesOnActiveSession()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        using var context = CreateInMemoryContext(connection);
        var service = new GatheringSessionService(context);
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
        var service = new GatheringSessionService(context);
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
        var service = new GatheringSessionService(context);
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
        var service = new GatheringSessionService(context);
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
        var service = new GatheringSessionService(context);
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
        var service = new GatheringSessionService(context);
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
        var service = new GatheringSessionService(context);
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
        var service = new GatheringSessionService(context);
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
        var service = new GatheringSessionService(context);
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
        var service = new GatheringSessionService(context);
        var raiseCount = 0;
        service.OnFameAdded += (_, _) => raiseCount++;

        await service.AddFameAsync("Gathering", 300);

        Assert.Equal(0, raiseCount);
    }
}
