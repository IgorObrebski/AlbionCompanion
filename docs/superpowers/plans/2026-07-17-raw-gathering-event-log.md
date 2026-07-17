# Raw Gathering Event Log Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Persist every raw Photon event to a new `RawGatheringEvents` table, independent of the existing interpreted `GatheredItem`/`FameLog` tables, so no field is ever lost to a bad interpretation decision made today.

**Architecture:** A new EF Core entity `RawGatheringEvent` plus a `RawEventRecorder` service that subscribes to `IPhotonParser.OnEventReceived` independently of the existing `GatheringEventRouter`. It records every event verbatim (Photon code, semantic event code if present, full parameter dictionary as JSON, and the currently active session id if any) without interpreting or filtering anything.

**Tech Stack:** .NET 10 / EF Core (Sqlite), System.Text.Json, xUnit.

## Global Constraints

- Spec source: `docs/superpowers/specs/2026-07-17-raw-gathering-event-log-design.md`.
- Every Photon event is recorded — no event-code allow-list, no actor filtering.
- `SessionId` is nullable; events with no active `GatheringSession` are still recorded, with `SessionId = null`.
- Existing tables/services (`GatheredItem`, `FameLog`, `GatheringSession`, `GatheringSessionService`, `GatheringEventRouter`, `HarvestableNodeTracker`) must not change behavior.

---

### Task 1: `RawGatheringEvent` model + `AppDbContext` wiring + migration

**Files:**
- Create: `AlbionCompanion.Core/Models/RawGatheringEvent.cs`
- Modify: `AlbionCompanion.Core/Data/AppDbContext.cs`
- Modify: `AlbionCompanion.Core.Tests/Data/AppDbContextTests.cs`
- Create (via `dotnet ef`): new migration under `AlbionCompanion.Core/Data/Migrations/`

**Interfaces:**
- Produces: `RawGatheringEvent` with properties `Id (long)`, `SessionId (Guid?)`, `Session (GatheringSession?)`, `PhotonCode (byte)`, `SemanticEventCode (byte?)`, `ParametersJson (string)`, `Timestamp (DateTime)`. Produces `AppDbContext.RawGatheringEvents` as `DbSet<RawGatheringEvent>`.

- [ ] **Step 1: Write the failing test for the new DbSet**

Add to `AlbionCompanion.Core.Tests/Data/AppDbContextTests.cs` (inside the existing `AppDbContextTests` class, after `AllDbSets_AreReachableAndPersistRoundTrip`):

```csharp
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
```

- [ ] **Step 2: Run tests to verify they fail to compile (no `RawGatheringEvent`/`RawGatheringEvents` yet)**

Run: `dotnet test AlbionCompanion.Core.Tests --filter FullyQualifiedName~AppDbContextTests`
Expected: build error — `RawGatheringEvent` / `RawGatheringEvents` does not exist.

- [ ] **Step 3: Create the model**

`AlbionCompanion.Core/Models/RawGatheringEvent.cs`:

```csharp
using System.ComponentModel.DataAnnotations;

namespace AlbionCompanion.Core.Models;

public class RawGatheringEvent
{
    [Key]
    public long Id { get; set; }
    public Guid? SessionId { get; set; }
    public GatheringSession? Session { get; set; }
    public byte PhotonCode { get; set; }
    public byte? SemanticEventCode { get; set; }
    public string ParametersJson { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
}
```

- [ ] **Step 4: Wire the DbSet and indexes into `AppDbContext`**

Modify `AlbionCompanion.Core/Data/AppDbContext.cs`:

```csharp
using AlbionCompanion.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace AlbionCompanion.Core.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<GatheringSession> GatheringSessions => Set<GatheringSession>();
    public DbSet<GatheredItem> GatheredItems => Set<GatheredItem>();
    public DbSet<FameLog> FameLogs => Set<FameLog>();
    public DbSet<FlipLog> FlipLogs => Set<FlipLog>();
    public DbSet<ItemDictionary> ItemDictionaries => Set<ItemDictionary>();
    public DbSet<PriceCache> PriceCaches => Set<PriceCache>();
    public DbSet<RawGatheringEvent> RawGatheringEvents => Set<RawGatheringEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PriceCache>().HasKey(priceCache => new { priceCache.ItemId, priceCache.Location });

        modelBuilder.Entity<RawGatheringEvent>(entity =>
        {
            entity.HasIndex(e => e.SessionId);
            entity.HasIndex(e => e.Timestamp);
            entity.HasOne(e => e.Session)
                .WithMany()
                .HasForeignKey(e => e.SessionId)
                .IsRequired(false);
        });
    }
}
```

- [ ] **Step 5: Run tests to verify they still fail (no migration applied yet)**

Run: `dotnet test AlbionCompanion.Core.Tests --filter FullyQualifiedName~AppDbContextTests`
Expected: compiles now, but `RawGatheringEvents_PersistRoundTrip_WithNullableSessionId` and `RawGatheringEvents_PersistWithSessionId` still need the table to exist — since these tests use `Database.EnsureCreated()` (not migrations) against the model, they should actually already pass at this point because `EnsureCreated()` builds the schema straight from the current model, not from migration history. Confirm this by running the tests: if they pass here, that's expected — `EnsureCreated()`-based tests validate the model directly. The migration in Step 6 is still required for the real app database (`Program.cs` calls `Database.MigrateAsync()`, not `EnsureCreated()`).

- [ ] **Step 6: Generate the EF Core migration**

Run: `dotnet ef migrations add AddRawGatheringEvents --project AlbionCompanion.Core --startup-project AlbionCompanion.Core`
Expected: new files created under `AlbionCompanion.Core/Data/Migrations/` (e.g. `<timestamp>_AddRawGatheringEvents.cs`, `.Designer.cs`) plus an updated `AppDbContextModelSnapshot.cs`, adding a `RawGatheringEvents` table with columns matching the model and an FK to `GatheringSessions`.

- [ ] **Step 7: Run the full Core test suite to confirm nothing regressed**

Run: `dotnet test AlbionCompanion.Core.Tests`
Expected: PASS (all tests, including the two new ones).

- [ ] **Step 8: Commit**

```bash
git add AlbionCompanion.Core/Models/RawGatheringEvent.cs AlbionCompanion.Core/Data/AppDbContext.cs AlbionCompanion.Core/Data/Migrations AlbionCompanion.Core.Tests/Data/AppDbContextTests.cs
git commit -m "feat(core): add RawGatheringEvent model and migration"
```

---

### Task 2: `IRawEventRecorder` / `RawEventRecorder` service

**Files:**
- Create: `AlbionCompanion.Gathering/IRawEventRecorder.cs`
- Create: `AlbionCompanion.Gathering/RawEventRecorder.cs`
- Create: `AlbionCompanion.Gathering.Tests/RawEventRecorderTests.cs`

**Interfaces:**
- Consumes: `IPhotonParser.OnEventReceived` (`EventHandler<PhotonEvent>`, `PhotonEvent(byte Code, Dictionary<byte, object?> Parameters)`) from `AlbionCompanion.Sniffer.Protocol16`; `IGatheringSessionService.GetActiveSessionAsync()` returning `Task<GatheringSession?>` from `AlbionCompanion.Gathering`; `AppDbContext.RawGatheringEvents` from `AlbionCompanion.Core.Data` (Task 1).
- Produces: `IRawEventRecorder` marker interface (no members — construction alone wires up the subscription, matching the existing pattern for `IHarvestableNodeTracker`/`ILocalPlayerTracker` where DI just needs a type to resolve). `RawEventRecorder.HandleEventAsync(PhotonEvent)` as an internal method exposed for direct testing (same pattern as `GatheringEventRouter.HandleEventAsync`).

- [ ] **Step 1: Write the failing tests**

`AlbionCompanion.Gathering.Tests/RawEventRecorderTests.cs`:

```csharp
using AlbionCompanion.Core.Data;
using AlbionCompanion.Core.Models;
using AlbionCompanion.Sniffer.Protocol16;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AlbionCompanion.Gathering.Tests;

public class RawEventRecorderTests
{
    private sealed class FakePhotonParser : IPhotonParser
    {
        public event EventHandler<PhotonEvent>? OnEventReceived;
        public event EventHandler<PhotonResponse>? OnResponseReceived;
        public event EventHandler<PhotonRequest>? OnRequestReceived;
        public void HandlePayload(byte[] payload) { }
        public void RaiseEvent(PhotonEvent photonEvent) => OnEventReceived?.Invoke(this, photonEvent);
    }

    private static (GatheringSessionService Service, AppDbContext Context) CreateServiceWithContext(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        var context = new AppDbContext(options);
        context.Database.EnsureCreated();
        var service = new GatheringSessionService(context);
        return (service, context);
    }

    [Fact]
    public async Task EventWithActiveSession_RecordsRowWithSessionId()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var (service, context) = CreateServiceWithContext(connection);
        await service.StartSessionAsync("4213");
        var session = await service.GetActiveSessionAsync();
        var parser = new FakePhotonParser();
        var recorder = new RawEventRecorder(parser, service, context);

        await recorder.HandleEventAsync(new PhotonEvent(1, new Dictionary<byte, object?> { [0] = 535802, [252] = (byte)59 }));

        var stored = Assert.Single(context.RawGatheringEvents);
        Assert.Equal(session!.Id, stored.SessionId);
        Assert.Equal((byte)1, stored.PhotonCode);
        Assert.Equal((byte)59, stored.SemanticEventCode);
    }

    [Fact]
    public async Task EventWithNoActiveSession_RecordsRowWithNullSessionId()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var (service, context) = CreateServiceWithContext(connection);
        var parser = new FakePhotonParser();
        var recorder = new RawEventRecorder(parser, service, context);

        await recorder.HandleEventAsync(new PhotonEvent(1, new Dictionary<byte, object?> { [0] = 535802 }));

        var stored = Assert.Single(context.RawGatheringEvents);
        Assert.Null(stored.SessionId);
    }

    [Fact]
    public async Task EventWithoutSemanticCodeParameter_RecordsNullSemanticEventCode()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var (service, context) = CreateServiceWithContext(connection);
        var parser = new FakePhotonParser();
        var recorder = new RawEventRecorder(parser, service, context);

        await recorder.HandleEventAsync(new PhotonEvent(3, new Dictionary<byte, object?> { [0] = 535802, [1] = 100 }));

        var stored = Assert.Single(context.RawGatheringEvents);
        Assert.Null(stored.SemanticEventCode);
    }

    [Fact]
    public async Task SemanticCodeOutOfByteRange_RecordsNullSemanticEventCode()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var (service, context) = CreateServiceWithContext(connection);
        var parser = new FakePhotonParser();
        var recorder = new RawEventRecorder(parser, service, context);

        await recorder.HandleEventAsync(new PhotonEvent(3, new Dictionary<byte, object?> { [252] = 99999 }));

        var stored = Assert.Single(context.RawGatheringEvents);
        Assert.Null(stored.SemanticEventCode);
    }

    [Fact]
    public async Task ParametersJson_RoundTripsOriginalDictionary()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var (service, context) = CreateServiceWithContext(connection);
        var parser = new FakePhotonParser();
        var recorder = new RawEventRecorder(parser, service, context);

        await recorder.HandleEventAsync(new PhotonEvent(1, new Dictionary<byte, object?> { [0] = 535802, [3] = 2955, [4] = 27, [252] = (byte)59 }));

        var stored = Assert.Single(context.RawGatheringEvents);
        var roundTripped = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(stored.ParametersJson);
        Assert.Equal(535802, roundTripped!["0"].GetInt32());
        Assert.Equal(2955, roundTripped["3"].GetInt32());
        Assert.Equal(27, roundTripped["4"].GetInt32());
        Assert.Equal(59, roundTripped["252"].GetInt32());
    }

    [Fact]
    public async Task EventReceivedThroughParserSubscription_IsRecorded()
    {
        // Confirms the constructor actually subscribes to OnEventReceived, not just that
        // HandleEventAsync works when called directly.
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var (service, context) = CreateServiceWithContext(connection);
        var parser = new FakePhotonParser();
        _ = new RawEventRecorder(parser, service, context);

        parser.RaiseEvent(new PhotonEvent(1, new Dictionary<byte, object?> { [252] = (byte)59 }));
        await Task.Delay(50); // event handler is fire-and-forget (async void-like dispatch)

        Assert.Single(context.RawGatheringEvents);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail (no `RawEventRecorder` yet)**

Run: `dotnet test AlbionCompanion.Gathering.Tests --filter FullyQualifiedName~RawEventRecorderTests`
Expected: build error — `RawEventRecorder` does not exist.

- [ ] **Step 3: Write the interface**

`AlbionCompanion.Gathering/IRawEventRecorder.cs`:

```csharp
namespace AlbionCompanion.Gathering;

// Marker interface: construction alone subscribes to IPhotonParser.OnEventReceived, mirroring
// IHarvestableNodeTracker/ILocalPlayerTracker - DI just needs a type to resolve and hold alive
// for the process lifetime.
public interface IRawEventRecorder
{
}
```

- [ ] **Step 4: Write the implementation**

`AlbionCompanion.Gathering/RawEventRecorder.cs`:

```csharp
using System.Text.Json;
using AlbionCompanion.Core.Data;
using AlbionCompanion.Core.Models;
using AlbionCompanion.Sniffer.Protocol16;

namespace AlbionCompanion.Gathering;

// Records every Photon event verbatim, independent of GatheringEventRouter's interpretation
// logic. No event-code filtering, no actor filtering: the point is to never lose a field to a
// bad interpretation decision made today. See
// docs/superpowers/specs/2026-07-17-raw-gathering-event-log-design.md.
public class RawEventRecorder : IRawEventRecorder
{
    private const byte SemanticEventCodeParameterKey = 252;

    private readonly IGatheringSessionService _sessionService;
    private readonly AppDbContext _dbContext;

    public RawEventRecorder(IPhotonParser photonParser, IGatheringSessionService sessionService, AppDbContext dbContext)
    {
        _sessionService = sessionService;
        _dbContext = dbContext;
        photonParser.OnEventReceived += (_, e) => _ = HandleEventAsync(e);
    }

    internal async Task HandleEventAsync(PhotonEvent photonEvent)
    {
        var session = await _sessionService.GetActiveSessionAsync();

        _dbContext.RawGatheringEvents.Add(new RawGatheringEvent
        {
            SessionId = session?.Id,
            PhotonCode = photonEvent.Code,
            SemanticEventCode = TryGetSemanticEventCode(photonEvent),
            ParametersJson = JsonSerializer.Serialize(photonEvent.Parameters.ToDictionary(p => p.Key.ToString(), p => p.Value)),
            Timestamp = DateTime.UtcNow,
        });

        await _dbContext.SaveChangesAsync();
    }

    private static byte? TryGetSemanticEventCode(PhotonEvent photonEvent)
    {
        if (!photonEvent.Parameters.TryGetValue(SemanticEventCodeParameterKey, out var value) || value is null)
        {
            return null;
        }

        var numeric = Convert.ToInt64(value);
        return numeric is >= byte.MinValue and <= byte.MaxValue ? (byte)numeric : null;
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test AlbionCompanion.Gathering.Tests --filter FullyQualifiedName~RawEventRecorderTests`
Expected: PASS (all 6 tests).

- [ ] **Step 6: Run the full Gathering test suite to confirm nothing regressed**

Run: `dotnet test AlbionCompanion.Gathering.Tests`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add AlbionCompanion.Gathering/IRawEventRecorder.cs AlbionCompanion.Gathering/RawEventRecorder.cs AlbionCompanion.Gathering.Tests/RawEventRecorderTests.cs
git commit -m "feat(gathering): add RawEventRecorder capturing every Photon event"
```

---

### Task 3: Wire `RawEventRecorder` into `Program.cs`

**Files:**
- Modify: `AlbionCompanion.ConsoleHost/Program.cs`

**Interfaces:**
- Consumes: `IRawEventRecorder`/`RawEventRecorder` (Task 2), existing `IGatheringSessionService`, `IPhotonParser`, `AppDbContext` registrations already present in `Program.cs`.

- [ ] **Step 1: Register `RawEventRecorder` in DI, scoped like `GatheringEventRouter`**

Modify `AlbionCompanion.ConsoleHost/Program.cs`. Change:

```csharp
services.AddScoped<ZoneTracker>();
services.AddScoped<GatheringEventRouter>();
```

to:

```csharp
services.AddScoped<ZoneTracker>();
services.AddScoped<GatheringEventRouter>();
services.AddScoped<IRawEventRecorder, RawEventRecorder>();
```

- [ ] **Step 2: Force construction in the same long-lived session scope as `GatheringEventRouter`**

Modify:

```csharp
var sessionScope = provider.CreateScope();
_ = sessionScope.ServiceProvider.GetRequiredService<ZoneTracker>();
_ = sessionScope.ServiceProvider.GetRequiredService<GatheringEventRouter>();
```

to:

```csharp
var sessionScope = provider.CreateScope();
_ = sessionScope.ServiceProvider.GetRequiredService<ZoneTracker>();
_ = sessionScope.ServiceProvider.GetRequiredService<GatheringEventRouter>();
_ = sessionScope.ServiceProvider.GetRequiredService<IRawEventRecorder>();
```

- [ ] **Step 3: Build to confirm no wiring errors**

Run: `dotnet build AlbionCompanion.ConsoleHost`
Expected: Build succeeded, 0 errors.

- [ ] **Step 4: Run the full solution test suite as a final regression check**

Run: `dotnet test`
Expected: PASS (all projects).

- [ ] **Step 5: Commit**

```bash
git add AlbionCompanion.ConsoleHost/Program.cs
git commit -m "feat(consolehost): wire RawEventRecorder alongside GatheringEventRouter"
```

---

## Self-Review Notes

- **Spec coverage:** model+migration (Task 1), independent recorder subscribing to every event with nullable SessionId (Task 2), DI wiring alongside the existing router without modifying it (Task 3), test coverage per the spec's Testing section (Tasks 1 & 2). No spec requirement is left uncovered.
- **Placeholders:** none — every step has literal file contents or exact commands.
- **Type consistency:** `RawGatheringEvent` properties (`Id`, `SessionId`, `Session`, `PhotonCode`, `SemanticEventCode`, `ParametersJson`, `Timestamp`) are identical across Task 1's model, `AppDbContext` wiring, and Task 2's `RawEventRecorder` usage. `RawEventRecorder` constructor signature `(IPhotonParser, IGatheringSessionService, AppDbContext)` matches its Task 2 test instantiations and Task 3's DI registration (all three are already registered independently in `Program.cs`).
