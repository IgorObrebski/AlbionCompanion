# GatheringSessionService Domain Events Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add `OnSessionStarted`, `OnSessionEnded`, `OnItemAdded`, `OnFameAdded` events to `IGatheringSessionService`/`GatheringSessionService`, firing only on real state changes, so a future UI can observe live session state without polling.

**Architecture:** Four plain `EventHandler<T>` events added to the existing interface and implementation, each raised synchronously right after `SaveChangesAsync()` on the branch that actually persisted a change — never on the existing no-op/ignore branches.

**Tech Stack:** C# / .NET, EF Core (SQLite), xUnit.

## Global Constraints

- `OnSessionStarted` fires only when a new session row is actually inserted — not on the "already active" no-op branch.
- `OnSessionEnded` fires only when the session is closed (`EndTime` set) — not on the "no active session" no-op, and not on the "empty session, discard it" branch.
- `OnItemAdded` / `OnFameAdded` fire only when there was an active session to attribute the row to — not on the "no active session, ignore" branch.
- No changes to existing method signatures, return types, or persistence behavior.
- `GatheringEventRouter` requires no changes — it stays unaware the service now raises events.

---

### Task 1: Add events to the interface and firing logic to the implementation

**Files:**
- Modify: `AlbionCompanion.Gathering/IGatheringSessionService.cs`
- Modify: `AlbionCompanion.Gathering/GatheringSessionService.cs`
- Test: `AlbionCompanion.Gathering.Tests/GatheringSessionServiceTests.cs`

**Interfaces:**
- Produces: `IGatheringSessionService.OnSessionStarted` (`EventHandler<GatheringSession>?`), `OnSessionEnded` (`EventHandler<GatheringSession>?`), `OnItemAdded` (`EventHandler<GatheredItem>?`), `OnFameAdded` (`EventHandler<FameLog>?`). These are the complete public surface this task adds; no later task in this plan builds on them further.

The full new `IGatheringSessionService.cs`:

```csharp
using AlbionCompanion.Core.Models;

namespace AlbionCompanion.Gathering;

public interface IGatheringSessionService
{
    Task StartSessionAsync(string location);
    Task EndSessionAsync();
    Task AddItemAsync(string itemId, int amount);
    Task AddFameAsync(string fameType, int amount);
    Task<GatheringSession?> GetActiveSessionAsync();

    event EventHandler<GatheringSession>? OnSessionStarted;
    event EventHandler<GatheringSession>? OnSessionEnded;
    event EventHandler<GatheredItem>? OnItemAdded;
    event EventHandler<FameLog>? OnFameAdded;
}
```

The full new `GatheringSessionService.cs`:

```csharp
using AlbionCompanion.Core.Data;
using AlbionCompanion.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace AlbionCompanion.Gathering;

public class GatheringSessionService : IGatheringSessionService
{
    private readonly AppDbContext _dbContext;

    public GatheringSessionService(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public event EventHandler<GatheringSession>? OnSessionStarted;
    public event EventHandler<GatheringSession>? OnSessionEnded;
    public event EventHandler<GatheredItem>? OnItemAdded;
    public event EventHandler<FameLog>? OnFameAdded;

    public async Task<GatheringSession?> GetActiveSessionAsync() =>
        await _dbContext.GatheringSessions.SingleOrDefaultAsync(session => session.EndTime == null);

    public async Task StartSessionAsync(string location)
    {
        // Invariant: at most one open (EndTime == null) session at a time - if one is already
        // active (e.g. a duplicate zone-change event, or resuming after a DC in the wilderness),
        // this is a no-op rather than starting a second concurrent session.
        if (await GetActiveSessionAsync() is not null)
        {
            return;
        }

        var session = new GatheringSession
        {
            StartTime = DateTime.UtcNow,
            StartLocation = location,
        };
        _dbContext.GatheringSessions.Add(session);

        await _dbContext.SaveChangesAsync();
        OnSessionStarted?.Invoke(this, session);
    }

    public async Task EndSessionAsync()
    {
        var session = await GetActiveSessionAsync();
        if (session is null)
        {
            return;
        }

        // An empty run (nothing gathered before returning to town) isn't worth keeping.
        // Queried directly by SessionId rather than via the session's navigation collections,
        // which GetActiveSessionAsync doesn't Include and so would always read as empty.
        var hasAnyActivity =
            await _dbContext.GatheredItems.AnyAsync(item => item.SessionId == session.Id) ||
            await _dbContext.FameLogs.AnyAsync(fame => fame.SessionId == session.Id);

        if (!hasAnyActivity)
        {
            _dbContext.GatheringSessions.Remove(session);
            await _dbContext.SaveChangesAsync();
            return;
        }

        session.EndTime = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync();
        OnSessionEnded?.Invoke(this, session);
    }

    public async Task AddItemAsync(string itemId, int amount)
    {
        var session = await GetActiveSessionAsync();
        if (session is null)
        {
            // No open wilderness session to attribute this to (e.g. picked up in town) - ignore.
            return;
        }

        var item = new GatheredItem
        {
            SessionId = session.Id,
            ItemId = itemId,
            Amount = amount,
            Timestamp = DateTime.UtcNow,
        };
        _dbContext.GatheredItems.Add(item);

        await _dbContext.SaveChangesAsync();
        OnItemAdded?.Invoke(this, item);
    }

    public async Task AddFameAsync(string fameType, int amount)
    {
        var session = await GetActiveSessionAsync();
        if (session is null)
        {
            return;
        }

        var fameLog = new FameLog
        {
            SessionId = session.Id,
            FameType = fameType,
            Amount = amount,
            Timestamp = DateTime.UtcNow,
        };
        _dbContext.FameLogs.Add(fameLog);
        session.TotalFameEarned += amount;

        await _dbContext.SaveChangesAsync();
        OnFameAdded?.Invoke(this, fameLog);
    }
}
```

- [ ] **Step 1: Write the failing tests**

Add to `AlbionCompanion.Gathering.Tests/GatheringSessionServiceTests.cs` (inside the existing `GatheringSessionServiceTests` class, alongside the existing facts):

```csharp
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
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test AlbionCompanion.Gathering.Tests --filter GatheringSessionServiceTests`
Expected: FAIL — build error, `IGatheringSessionService`/`GatheringSessionService` do not contain definitions for `OnSessionStarted`, `OnSessionEnded`, `OnItemAdded`, `OnFameAdded`.

- [ ] **Step 3: Replace both files with the implementations shown above**

Replace the full contents of `AlbionCompanion.Gathering/IGatheringSessionService.cs` and `AlbionCompanion.Gathering/GatheringSessionService.cs` with the code blocks given above.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test AlbionCompanion.Gathering.Tests --filter GatheringSessionServiceTests`
Expected: PASS — all facts in `GatheringSessionServiceTests`, including the 9 new ones, pass.

- [ ] **Step 5: Run the full test suite**

Run: `dotnet build && dotnet test`
Expected: Build succeeds, all tests across the solution PASS (no other project depends on the changed signatures beyond what's already covered — `GatheringEventRouter` calls the unchanged methods only).

- [ ] **Step 6: Commit**

```bash
git add AlbionCompanion.Gathering/IGatheringSessionService.cs AlbionCompanion.Gathering/GatheringSessionService.cs AlbionCompanion.Gathering.Tests/GatheringSessionServiceTests.cs
git commit -m "feat(gathering): add domain events to GatheringSessionService"
```

---

## Self-Review Notes

- Spec coverage: all four events added with exact firing/non-firing rules from the spec's Firing rules section; all 9 spec-listed test cases present (`StartSessionAsync` x2, `EndSessionAsync` x3, `AddItemAsync` x2, `AddFameAsync` x2). Non-goals (UI, cross-scope wiring) untouched — single task, single file pair.
- No placeholders — full file contents given for both modified files, full test code given for all 9 new facts.
- Type/name consistency: event names and types match the spec's interface exactly (`EventHandler<GatheringSession>?`, `EventHandler<GatheredItem>?`, `EventHandler<FameLog>?`); test assertions reference the same property names (`StartLocation`, `EndTime`, `ItemId`, `Amount`, `FameType`) already used by the existing tests in this file.
