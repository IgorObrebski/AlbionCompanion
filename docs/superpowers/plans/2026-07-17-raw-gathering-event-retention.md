# Raw Gathering Event Retention Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Delete `RawGatheringEvents` rows older than 7 days once at application startup, so the debug-only raw event log stays bounded in size.

**Architecture:** A small static holder class exposes the retention period as a `TimeSpan` constant. `Program.cs` runs a single `ExecuteDeleteAsync()` query against `AppDbContext.RawGatheringEvents` inside the existing startup `migrationScope` block, right after migration and seeding, before event capture begins.

**Tech Stack:** C# / .NET, EF Core (SQLite), xUnit.

## Global Constraints

- Retention period is a fixed constant of 7 days — no configuration surface (per spec Non-goals).
- Only `RawGatheringEvents` is targeted — no other table's retention changes (per spec Non-goals).
- No periodic/background cleanup — startup-only sweep (per spec Non-goals).
- No special error handling around the delete — a failure propagates and fails startup, matching how `MigrateAsync()` already behaves (per spec's Error handling section).

---

### Task 1: Retention constant holder

**Files:**
- Create: `AlbionCompanion.Gathering/RawGatheringEventRetention.cs`
- Test: `AlbionCompanion.Gathering.Tests/RawGatheringEventRetentionTests.cs`

**Interfaces:**
- Produces: `AlbionCompanion.Gathering.RawGatheringEventRetention.Period` — `public static readonly TimeSpan`, value `TimeSpan.FromDays(7)`. Consumed by Task 2 (`Program.cs`) and Task 3 (cleanup test).

- [ ] **Step 1: Write the failing test**

```csharp
using Xunit;

namespace AlbionCompanion.Gathering.Tests;

public class RawGatheringEventRetentionTests
{
    [Fact]
    public void Period_IsSevenDays()
    {
        Assert.Equal(TimeSpan.FromDays(7), RawGatheringEventRetention.Period);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test AlbionCompanion.Gathering.Tests --filter RawGatheringEventRetentionTests`
Expected: FAIL — build error, `RawGatheringEventRetention` does not exist.

- [ ] **Step 3: Write minimal implementation**

```csharp
namespace AlbionCompanion.Gathering;

public static class RawGatheringEventRetention
{
    public static readonly TimeSpan Period = TimeSpan.FromDays(7);
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test AlbionCompanion.Gathering.Tests --filter RawGatheringEventRetentionTests`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add AlbionCompanion.Gathering/RawGatheringEventRetention.cs AlbionCompanion.Gathering.Tests/RawGatheringEventRetentionTests.cs
git commit -m "feat(gathering): add RawGatheringEvent retention period constant"
```

---

### Task 2: Startup cleanup sweep

**Files:**
- Modify: `AlbionCompanion.ConsoleHost/Program.cs:93-98`
- Test: `AlbionCompanion.Gathering.Tests/RawGatheringEventRetentionTests.cs` (extend from Task 1)

**Interfaces:**
- Consumes: `AlbionCompanion.Gathering.RawGatheringEventRetention.Period` (Task 1); `AppDbContext.RawGatheringEvents` (`DbSet<RawGatheringEvent>`, existing); `RawGatheringEvent.Timestamp` (`DateTime`, existing, UTC per `RawEventRecorder.cs:53`).
- Produces: nothing consumed by later tasks — this is the final integration point.

The current block at `Program.cs:93-98`:

```csharp
using (var migrationScope = provider.CreateScope())
{
    await migrationScope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
    Console.WriteLine("Checking item dictionary...");
    await migrationScope.ServiceProvider.GetRequiredService<IItemDictionaryService>().SeedFromJsonAsync();
}
```

becomes:

```csharp
using (var migrationScope = provider.CreateScope())
{
    var dbContext = migrationScope.ServiceProvider.GetRequiredService<AppDbContext>();
    await dbContext.Database.MigrateAsync();
    Console.WriteLine("Checking item dictionary...");
    await migrationScope.ServiceProvider.GetRequiredService<IItemDictionaryService>().SeedFromJsonAsync();

    var rawEventCutoff = DateTime.UtcNow - RawGatheringEventRetention.Period;
    await dbContext.RawGatheringEvents
        .Where(e => e.Timestamp < rawEventCutoff)
        .ExecuteDeleteAsync();
}
```

`ExecuteDeleteAsync` is on `Microsoft.EntityFrameworkCore`, already `using`'d in `Program.cs` (it's needed for `MigrateAsync`/EF Core types already in that file). `AlbionCompanion.Gathering` is already referenced by `AlbionCompanion.ConsoleHost` (it constructs `RawEventRecorder`/`GatheringEventRouter` from that namespace), so no new project reference is needed.

This integration is exercised end-to-end via a test that mirrors the delete query directly (`AppDbContextTests`-style, in-memory SQLite), since `Program.cs`'s `Main` isn't itself unit-testable — the query logic is what needs verifying, and it's the same query that will run in `Program.cs`.

- [ ] **Step 1: Write the failing test**

Add to `AlbionCompanion.Gathering.Tests/RawGatheringEventRetentionTests.cs`:

```csharp
using AlbionCompanion.Core.Data;
using AlbionCompanion.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

// (keep existing usings and the Period_IsSevenDays fact above, add this fact to the same class)

[Fact]
public async Task CleanupSweep_DeletesOnlyRowsOlderThanRetentionPeriod()
{
    using var connection = new SqliteConnection("DataSource=:memory:");
    connection.Open();
    var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
    using var context = new AppDbContext(options);
    context.Database.EnsureCreated();

    var oldRow = new RawGatheringEvent
    {
        PhotonCode = 1,
        ParametersJson = "{}",
        Timestamp = DateTime.UtcNow - RawGatheringEventRetention.Period - TimeSpan.FromDays(1),
    };
    var newRow = new RawGatheringEvent
    {
        PhotonCode = 2,
        ParametersJson = "{}",
        Timestamp = DateTime.UtcNow - TimeSpan.FromDays(1),
    };
    context.RawGatheringEvents.AddRange(oldRow, newRow);
    await context.SaveChangesAsync();

    var cutoff = DateTime.UtcNow - RawGatheringEventRetention.Period;
    await context.RawGatheringEvents
        .Where(e => e.Timestamp < cutoff)
        .ExecuteDeleteAsync();

    var remaining = Assert.Single(context.RawGatheringEvents);
    Assert.Equal(newRow.PhotonCode, remaining.PhotonCode);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test AlbionCompanion.Gathering.Tests --filter CleanupSweep_DeletesOnlyRowsOlderThanRetentionPeriod`
Expected: FAIL — `ExecuteDeleteAsync` against SQLite in-memory requires no extra setup beyond what `AppDbContextTests` already uses, so this should fail only if the query logic is wrong; run it first to confirm it fails for the right reason isn't applicable here since there's no production code gap — skip ahead if it passes immediately (the test validates the query shape itself, which Step 3 below moves into `Program.cs`).

Note: unlike Task 1, this test can pass immediately since the query is self-contained EF Core logic, not new production code — that's expected. Proceed to Step 3 to wire the identical query into `Program.cs`.

- [ ] **Step 3: Wire the identical query into Program.cs**

Apply the `Program.cs:93-98` edit shown above.

- [ ] **Step 4: Run the full test suite and build**

Run: `dotnet build && dotnet test AlbionCompanion.Gathering.Tests`
Expected: Build succeeds, all tests PASS (including the new `CleanupSweep_DeletesOnlyRowsOlderThanRetentionPeriod`).

- [ ] **Step 5: Commit**

```bash
git add AlbionCompanion.ConsoleHost/Program.cs AlbionCompanion.Gathering.Tests/RawGatheringEventRetentionTests.cs
git commit -m "feat(consolehost): delete RawGatheringEvent rows older than retention period on startup"
```

---

## Self-Review Notes

- Spec coverage: retention constant (Task 1), startup-trigger cleanup via `ExecuteDeleteAsync` in the existing `migrationScope` block (Task 2), test asserting old rows removed / new rows kept (Task 2) — all spec sections covered. No config surface, no other tables touched, no background scheduler — matches Non-goals.
- No placeholders — all steps have literal file contents and exact commands.
- Type/name consistency checked: `RawGatheringEventRetention.Period` used identically in Task 1 and Task 2; `RawGatheringEvent.Timestamp`, `PhotonCode`, `ParametersJson` match the existing model (`AlbionCompanion.Core/Models/RawGatheringEvent.cs`) and existing test style (`RawEventRecorderTests.cs`).
