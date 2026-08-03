# Multi-Character Support Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give AlbionCompanion a `Character` entity that gathering sessions attach to automatically (identified from live-capture-confirmed Photon signals, no manual picker), plus a character-hub landing page, a per-character dashboard, and a character-scoped live view ("Broadcast").

**Architecture:** `LocalPlayerTracker` grows a second piece of state (`CurrentCharacterName`) fed by two Photon signals — the already-trusted zone-join response and a periodic nearby-player broadcast matched against registered character names. `GatheringSessionService.StartSessionAsync` resolves that name to a `Character.Id` at session-creation time and stamps it on the new `GatheringSession.CharacterId` (nullable FK). Everything downstream (live state, history queries, UI) threads that id through using the exact same patterns already established for `Location`/`Fame`/`Silver` breakdowns earlier this session.

**Tech Stack:** .NET 10, EF Core (SQLite), Blazor Hybrid (MAUI), xUnit.

**Design doc:** `docs/superpowers/specs/2026-08-03-multi-character-support-design.md` — read it first if anything below is ambiguous.

## Global Constraints

- One session belongs to at most one character for its whole lifetime — never reassigned mid-session.
- Existing sessions get `CharacterId = null` on migration — never retroactively guessed at.
- Deleting a `Character` detaches its sessions (`CharacterId` → `null`), never cascade-deletes them (mirrors the existing `RawGatheringEvent.SessionId` `SetNull` pattern in `AppDbContext.cs`).
- `Character.Name` is unique (DB-enforced via a unique index) — no app-level pre-check, let `DbUpdateException` surface and have the UI catch it.
- The Home/live view is relabeled **"Broadcast"** everywhere in the UI (nav, headings) — not "Home" or "Live".
- Every new async event handler follows this codebase's existing `OnError` + fire-and-forget pattern (see `GatheringEventRouter.OnError`, `ZoneTracker.OnError`) — no bare `_ = SomeAsync()` without an error-surfacing path.
- Run `dotnet test AlbionCompanion.Core.Tests/AlbionCompanion.Core.Tests.csproj` and `dotnet test AlbionCompanion.Gathering.Tests/AlbionCompanion.Gathering.Tests.csproj` after every task — both must stay green throughout.
- If `dotnet build` fails with `MSB3027`/`MSB3021` (file lock), it means `AlbionCompanion.App.exe` or `AlbionCompanion.ConsoleHost.exe` is still running — ask the user to close it before retrying. Never treat this as a real compile error.

---

### Task 1: `Character` entity, `AppDbContext`, migration

**Files:**
- Create: `AlbionCompanion.Core/Models/Character.cs`
- Modify: `AlbionCompanion.Core/Models/GatheringSession.cs`
- Modify: `AlbionCompanion.Core/Data/AppDbContext.cs`
- Create: EF migration (via `dotnet ef migrations add`)
- Test: `AlbionCompanion.Core.Tests/Data/AppDbContextTests.cs`

**Interfaces:**
- Produces: `Character { Guid Id; string Name; DateTime CreatedAt; }`, `GatheringSession.CharacterId` (`Guid?`), `GatheringSession.Character` (`Character?`), `AppDbContext.Characters` (`DbSet<Character>`).

- [ ] **Step 1: Write the failing tests**

Append to `AlbionCompanion.Core.Tests/Data/AppDbContextTests.cs`, inside the `AppDbContextTests` class, after the existing `RawGatheringEvents_PersistWithSessionId` test:

```csharp
    [Fact]
    public void Character_EnforcesNameUniqueness()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        using (var firstContext = CreateInMemoryContext(connection))
        {
            firstContext.Characters.Add(new Character { Name = "Ejnsztain", CreatedAt = DateTime.UtcNow });
            firstContext.SaveChanges();
        }

        using var secondContext = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        secondContext.Characters.Add(new Character { Name = "Ejnsztain", CreatedAt = DateTime.UtcNow });

        Assert.Throws<DbUpdateException>(() => secondContext.SaveChanges());
    }

    [Fact]
    public void GatheringSession_CharacterDeleted_SetsCharacterIdNullInsteadOfDeletingSession()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        using var context = CreateInMemoryContext(connection);

        var character = new Character { Name = "Ejnsztain", CreatedAt = DateTime.UtcNow };
        context.Characters.Add(character);
        context.GatheringSessions.Add(new GatheringSession
        {
            StartTime = DateTime.UtcNow,
            StartLocation = "Martlock",
            CharacterId = character.Id,
        });
        context.SaveChanges();

        context.Characters.Remove(character);
        context.SaveChanges();

        var session = Assert.Single(context.GatheringSessions);
        Assert.Null(session.CharacterId);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test AlbionCompanion.Core.Tests/AlbionCompanion.Core.Tests.csproj`
Expected: FAIL with `CS0117` ("Character does not exist in the current context" / `AppDbContext` has no `Characters`) — the type doesn't exist yet.

- [ ] **Step 3: Create the `Character` model**

Write `AlbionCompanion.Core/Models/Character.cs`:

```csharp
using System.ComponentModel.DataAnnotations;

namespace AlbionCompanion.Core.Models;

public class Character
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();
    // Exact in-game character name - matched against the nickname carried by the zone-join
    // response and the periodic PlayerAnnounce broadcast (see LocalPlayerTracker) to identify
    // which character is currently playing, without any manual "who am I" picker.
    public string Name { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}
```

- [ ] **Step 4: Add `CharacterId`/`Character` to `GatheringSession`**

In `AlbionCompanion.Core/Models/GatheringSession.cs`, add after the `TotalSilverEarned` property:

```csharp
    public int TotalSilverEarned { get; set; }
    // Which character earned this session's activity - null for sessions recorded before
    // multi-character support existed, or for an unregistered character (see LocalPlayerTracker).
    // Never reassigned mid-session; a session belongs to at most one character for its whole life.
    public Guid? CharacterId { get; set; }
    public Character? Character { get; set; }
    public ICollection<GatheredItem> GatheredItems { get; set; } = new List<GatheredItem>();
```

(This replaces the existing `public int TotalSilverEarned { get; set; }` line and the line right after it — insert the two new lines between `TotalSilverEarned` and `GatheredItems`.)

- [ ] **Step 5: Wire up `AppDbContext`**

In `AlbionCompanion.Core/Data/AppDbContext.cs`, add the new `DbSet` after `FameLogs`:

```csharp
    public DbSet<FameLog> FameLogs => Set<FameLog>();
    public DbSet<SilverLog> SilverLogs => Set<SilverLog>();
    public DbSet<Character> Characters => Set<Character>();
```

In `OnModelCreating`, add after the `RawGatheringEvent` configuration block (before the closing `}` of the method):

```csharp
        modelBuilder.Entity<Character>().HasIndex(c => c.Name).IsUnique();

        modelBuilder.Entity<GatheringSession>()
            .HasOne(s => s.Character)
            .WithMany()
            .HasForeignKey(s => s.CharacterId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.SetNull);
```

- [ ] **Step 6: Generate the migration**

Run: `dotnet ef migrations add AddCharacters --project AlbionCompanion.Core --startup-project AlbionCompanion.Core`
Expected: creates `AlbionCompanion.Core/Data/Migrations/<timestamp>_AddCharacters.cs` and `.Designer.cs`, and updates `AppDbContextModelSnapshot.cs`. Open the generated migration and confirm it creates a `Characters` table, a unique index on `Characters.Name`, and adds a nullable `CharacterId` column + FK with `ON DELETE SET NULL` to `GatheringSessions`.

- [ ] **Step 7: Run tests to verify they pass**

Run: `dotnet test AlbionCompanion.Core.Tests/AlbionCompanion.Core.Tests.csproj`
Expected: PASS, all tests including the two new ones.

- [ ] **Step 8: Commit**

```bash
git add AlbionCompanion.Core/Models/Character.cs AlbionCompanion.Core/Models/GatheringSession.cs AlbionCompanion.Core/Data/AppDbContext.cs AlbionCompanion.Core/Data/Migrations/ AlbionCompanion.Core.Tests/Data/AppDbContextTests.cs
git commit -m "feat(core): add Character entity and GatheringSession.CharacterId"
```

---

### Task 2: `ICharacterService` / `CharacterService` — CRUD + per-character overview

**Files:**
- Create: `AlbionCompanion.Gathering/ICharacterService.cs`
- Create: `AlbionCompanion.Gathering/CharacterService.cs`
- Test: `AlbionCompanion.Gathering.Tests/CharacterServiceTests.cs`

**Interfaces:**
- Consumes: `AppDbContext.Characters`/`.GatheringSessions`/`.GatheredItems` (Task 1), `IDbContextFactory<AppDbContext>` (already registered in `AppHostBuilder.cs`).
- Produces: `ICharacterService.GetAllAsync()`, `.AddAsync(string name)`, `.DeleteAsync(Guid id)`, `.GetAllOverviewsAsync()`, `.GetOverviewAsync(Guid characterId)`; `CharacterOverview(Guid Id, string Name, int TotalFameEarned, int TotalSilverEarned, int TotalItemsCollected, DateTime? LastActive, bool HasActiveSession)`.

- [ ] **Step 1: Write the failing tests**

Write `AlbionCompanion.Gathering.Tests/CharacterServiceTests.cs`:

```csharp
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
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test AlbionCompanion.Gathering.Tests/AlbionCompanion.Gathering.Tests.csproj --filter CharacterServiceTests`
Expected: FAIL to compile — `CharacterService`/`ICharacterService` don't exist yet.

- [ ] **Step 3: Write `ICharacterService`**

Write `AlbionCompanion.Gathering/ICharacterService.cs`:

```csharp
using AlbionCompanion.Core.Models;

namespace AlbionCompanion.Gathering;

public interface ICharacterService
{
    Task<IReadOnlyList<Character>> GetAllAsync();
    Task<Character> AddAsync(string name);
    Task DeleteAsync(Guid id);
    Task<IReadOnlyList<CharacterOverview>> GetAllOverviewsAsync();
    Task<CharacterOverview?> GetOverviewAsync(Guid characterId);
}

// One character's aggregate stats across every session it's attached to - the character hub
// card and the character dashboard's summary cards both read this shape.
public record CharacterOverview(
    Guid Id,
    string Name,
    int TotalFameEarned,
    int TotalSilverEarned,
    int TotalItemsCollected,
    DateTime? LastActive,
    bool HasActiveSession);
```

- [ ] **Step 4: Write `CharacterService`**

Write `AlbionCompanion.Gathering/CharacterService.cs`:

```csharp
using AlbionCompanion.Core.Data;
using AlbionCompanion.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace AlbionCompanion.Gathering;

// Mirrors ItemDictionaryService's shape (IDbContextFactory, registered as a Singleton) - see
// AppHostBuilder.cs. LocalPlayerTracker (also a Singleton) depends on this for its cold-start
// character-name match, so this can't be Scoped.
public class CharacterService : ICharacterService
{
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;

    public CharacterService(IDbContextFactory<AppDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory;
    }

    public async Task<IReadOnlyList<Character>> GetAllAsync()
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        return await dbContext.Characters.OrderBy(c => c.Name).ToListAsync();
    }

    public async Task<Character> AddAsync(string name)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();

        var character = new Character { Name = name, CreatedAt = DateTime.UtcNow };
        dbContext.Characters.Add(character);
        await dbContext.SaveChangesAsync();

        return character;
    }

    public async Task DeleteAsync(Guid id)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();

        var character = await dbContext.Characters.FindAsync(id);
        if (character is null)
        {
            return;
        }

        dbContext.Characters.Remove(character);
        await dbContext.SaveChangesAsync();
    }

    public async Task<IReadOnlyList<CharacterOverview>> GetAllOverviewsAsync()
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();

        var characters = await dbContext.Characters.OrderBy(c => c.Name).ToListAsync();
        var activeCharacterId = await GetActiveCharacterIdAsync(dbContext);

        // A handful of characters at most (realistically 1-10) - a per-character round trip in
        // a loop is simpler to read and test than one mega-query, and this isn't a hot path
        // (called once per hub-page load).
        var overviews = new List<CharacterOverview>();
        foreach (var character in characters)
        {
            overviews.Add(await BuildOverviewAsync(dbContext, character, activeCharacterId));
        }

        return overviews;
    }

    public async Task<CharacterOverview?> GetOverviewAsync(Guid characterId)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();

        var character = await dbContext.Characters.FindAsync(characterId);
        if (character is null)
        {
            return null;
        }

        var activeCharacterId = await GetActiveCharacterIdAsync(dbContext);
        return await BuildOverviewAsync(dbContext, character, activeCharacterId);
    }

    private static async Task<Guid?> GetActiveCharacterIdAsync(AppDbContext dbContext) =>
        await dbContext.GatheringSessions
            .Where(session => session.EndTime == null)
            .Select(session => session.CharacterId)
            .FirstOrDefaultAsync();

    private static async Task<CharacterOverview> BuildOverviewAsync(AppDbContext dbContext, Character character, Guid? activeCharacterId)
    {
        var sessions = dbContext.GatheringSessions.Where(session => session.CharacterId == character.Id);

        var totalFame = await sessions.SumAsync(session => (int?)session.TotalFameEarned) ?? 0;
        var totalSilver = await sessions.SumAsync(session => (int?)session.TotalSilverEarned) ?? 0;
        var totalItems = await dbContext.GatheredItems
            .Where(item => item.Session!.CharacterId == character.Id)
            .SumAsync(item => (int?)item.Amount) ?? 0;
        var lastActive = await sessions.MaxAsync(session => (DateTime?)session.StartTime);

        return new CharacterOverview(
            character.Id,
            character.Name,
            totalFame,
            totalSilver,
            totalItems,
            lastActive,
            character.Id == activeCharacterId);
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test AlbionCompanion.Gathering.Tests/AlbionCompanion.Gathering.Tests.csproj --filter CharacterServiceTests`
Expected: PASS, all 8 tests.

- [ ] **Step 6: Commit**

```bash
git add AlbionCompanion.Gathering/ICharacterService.cs AlbionCompanion.Gathering/CharacterService.cs AlbionCompanion.Gathering.Tests/CharacterServiceTests.cs
git commit -m "feat(gathering): add ICharacterService with CRUD and per-character overview aggregation"
```

---

### Task 3: `LocalPlayerTracker` tracks the current character's name (fixes the same-zone-restart bug)

**Files:**
- Modify: `AlbionCompanion.Sniffer/AlbionEvents/AlbionEventCode.cs`
- Modify: `AlbionCompanion.Gathering/ILocalPlayerTracker.cs`
- Modify: `AlbionCompanion.Gathering/LocalPlayerTracker.cs`
- Test: `AlbionCompanion.Gathering.Tests/LocalPlayerTrackerTests.cs`
- Test (fix existing fake): `AlbionCompanion.Gathering.Tests/GatheringEventRouterTests.cs`

**Interfaces:**
- Consumes: `ICharacterService.GetAllAsync()` (Task 2).
- Produces: `ILocalPlayerTracker.CurrentCharacterName` (`string?`), `ILocalPlayerTracker.OnError` (`EventHandler<Exception>?`).

- [ ] **Step 1: Write the failing tests**

Replace the full contents of `AlbionCompanion.Gathering.Tests/LocalPlayerTrackerTests.cs`:

```csharp
using AlbionCompanion.Core.Models;
using AlbionCompanion.Sniffer.Protocol16;
using Xunit;

namespace AlbionCompanion.Gathering.Tests;

public class LocalPlayerTrackerTests
{
    private sealed class FakePhotonParser : IPhotonParser
    {
        public event EventHandler<PhotonEvent>? OnEventReceived;
        public event EventHandler<PhotonResponse>? OnResponseReceived;
        public event EventHandler<PhotonRequest>? OnRequestReceived;
        public void HandlePayload(byte[] payload) { }
        public void RaiseResponse(PhotonResponse response) => OnResponseReceived?.Invoke(this, response);
        public void RaiseEvent(PhotonEvent photonEvent) => OnEventReceived?.Invoke(this, photonEvent);
    }

    private sealed class FakeCharacterService : ICharacterService
    {
        public List<Character> Characters { get; } = new();

        public Task<IReadOnlyList<Character>> GetAllAsync() => Task.FromResult<IReadOnlyList<Character>>(Characters);
        public Task<Character> AddAsync(string name) => throw new NotImplementedException();
        public Task DeleteAsync(Guid id) => throw new NotImplementedException();
        public Task<IReadOnlyList<CharacterOverview>> GetAllOverviewsAsync() => throw new NotImplementedException();
        public Task<CharacterOverview?> GetOverviewAsync(Guid characterId) => throw new NotImplementedException();
    }

    private static PhotonResponse ZoneJoinResponse(int ownEntityId, string nickname = "Ejnsztain") =>
        new(1, 0, string.Empty, new Dictionary<byte, object?> { [0] = ownEntityId, [2] = nickname, [253] = 2 });

    private static PhotonEvent PlayerAnnounce(int entityId, string nickname) =>
        new(1, new Dictionary<byte, object?> { [0] = entityId, [2] = nickname, [252] = (byte)279 });

    [Fact]
    public void ZoneJoinResponse_RecordsOwnEntityId()
    {
        var parser = new FakePhotonParser();
        var tracker = new LocalPlayerTracker(parser, new FakeCharacterService());

        parser.RaiseResponse(ZoneJoinResponse(200760));

        Assert.Equal(200760, tracker.CurrentEntityId);
    }

    [Fact]
    public void ZoneJoinResponse_AlsoRecordsCharacterName()
    {
        // The zone-join response is a RESPONSE to our own REQUEST, so it's inherently self-only -
        // it carries our nickname too, which PlayerAnnounce (code 279, broadcast to everyone
        // nearby) later needs to match against.
        var parser = new FakePhotonParser();
        var tracker = new LocalPlayerTracker(parser, new FakeCharacterService());

        parser.RaiseResponse(ZoneJoinResponse(200760, "Ejnsztain"));

        Assert.Equal("Ejnsztain", tracker.CurrentCharacterName);
    }

    [Fact]
    public void SubsequentZoneJoin_UpdatesToTheNewEntityId()
    {
        // Regression: entity ids are reassigned per zone, not stable across zone changes -
        // confirmed via live capture where the same character got a different id on each join.
        var parser = new FakePhotonParser();
        var tracker = new LocalPlayerTracker(parser, new FakeCharacterService());

        parser.RaiseResponse(ZoneJoinResponse(200760));
        parser.RaiseResponse(ZoneJoinResponse(1111937));

        Assert.Equal(1111937, tracker.CurrentEntityId);
    }

    [Fact]
    public void ResponseWithoutZoneJoinSubCode_IsIgnored()
    {
        var parser = new FakePhotonParser();
        var tracker = new LocalPlayerTracker(parser, new FakeCharacterService());

        parser.RaiseResponse(new PhotonResponse(1, 0, string.Empty,
            new Dictionary<byte, object?> { [0] = 999, [253] = 52 }));

        Assert.Null(tracker.CurrentEntityId);
    }

    [Fact]
    public async Task PlayerAnnounce_MatchingConfirmedCharacterName_RefreshesEntityId()
    {
        // Confirmed live 2026-08-03: PlayerAnnounce (code 279) fires periodically, independent of
        // zone transitions - this is what lets CurrentEntityId recover without a zone change, the
        // known same-zone-restart bug's fix.
        var parser = new FakePhotonParser();
        var tracker = new LocalPlayerTracker(parser, new FakeCharacterService());
        parser.RaiseResponse(ZoneJoinResponse(200760, "Ejnsztain"));

        parser.RaiseEvent(PlayerAnnounce(entityId: 41390, nickname: "Ejnsztain"));
        await Task.Delay(10);

        Assert.Equal(41390, tracker.CurrentEntityId);
    }

    [Fact]
    public async Task PlayerAnnounce_WithNonMatchingNickname_IsIgnored()
    {
        // Confirmed live 2026-08-03: PlayerAnnounce broadcasts for any nearby player, not just
        // the local one - two different nicknames were observed for two different entities in
        // the same capture window.
        var parser = new FakePhotonParser();
        var tracker = new LocalPlayerTracker(parser, new FakeCharacterService());
        parser.RaiseResponse(ZoneJoinResponse(200760, "Ejnsztain"));

        parser.RaiseEvent(PlayerAnnounce(entityId: 107157, nickname: "Valdekir"));
        await Task.Delay(10);

        Assert.Equal(200760, tracker.CurrentEntityId);
    }

    [Fact]
    public async Task PlayerAnnounce_BeforeAnyZoneJoin_MatchingRegisteredCharacter_AdoptsIdentity()
    {
        // The cold-start case: the app was just restarted in the same zone, so no zone-join
        // response has fired yet - the only way to recover identity without a zone transition is
        // to trust a PlayerAnnounce whose nickname matches a character the user has registered.
        var parser = new FakePhotonParser();
        var characterService = new FakeCharacterService();
        characterService.Characters.Add(new Character { Name = "Ejnsztain", CreatedAt = DateTime.UtcNow });
        var tracker = new LocalPlayerTracker(parser, characterService);

        parser.RaiseEvent(PlayerAnnounce(entityId: 41390, nickname: "Ejnsztain"));
        await Task.Delay(10);

        Assert.Equal(41390, tracker.CurrentEntityId);
        Assert.Equal("Ejnsztain", tracker.CurrentCharacterName);
    }

    [Fact]
    public async Task PlayerAnnounce_BeforeAnyZoneJoin_UnregisteredNickname_IsIgnored()
    {
        var parser = new FakePhotonParser();
        var tracker = new LocalPlayerTracker(parser, new FakeCharacterService());

        parser.RaiseEvent(PlayerAnnounce(entityId: 41390, nickname: "SomeoneElse"));
        await Task.Delay(10);

        Assert.Null(tracker.CurrentEntityId);
        Assert.Null(tracker.CurrentCharacterName);
    }

    [Fact]
    public void OtherSemanticEventCode_IsIgnored()
    {
        var parser = new FakePhotonParser();
        var tracker = new LocalPlayerTracker(parser, new FakeCharacterService());

        parser.RaiseEvent(new PhotonEvent(1, new Dictionary<byte, object?> { [0] = 41390, [2] = "Ejnsztain", [252] = (byte)61 }));

        Assert.Null(tracker.CurrentEntityId);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test AlbionCompanion.Gathering.Tests/AlbionCompanion.Gathering.Tests.csproj --filter LocalPlayerTrackerTests`
Expected: FAIL to compile — `LocalPlayerTracker`'s constructor doesn't take an `ICharacterService` yet, `CurrentCharacterName` doesn't exist, `AlbionEventCode.PlayerAnnounce` doesn't exist.

- [ ] **Step 3: Add `PlayerAnnounce` to `AlbionEventCode`**

In `AlbionCompanion.Sniffer/AlbionEvents/AlbionEventCode.cs`, add after `UpdateFame = 82,`:

```csharp
    UpdateFame = 82,
    // Unconfirmed name - a periodic broadcast pairing (entityId, nickname) for any nearby
    // player (not self-only, confirmed live 2026-08-03: two different nicknames seen for two
    // different entities in one capture window). See LocalPlayerTracker for how this is used.
    PlayerAnnounce = 279,
```

- [ ] **Step 4: Update `ILocalPlayerTracker`**

Replace the full contents of `AlbionCompanion.Gathering/ILocalPlayerTracker.cs`:

```csharp
namespace AlbionCompanion.Gathering;

public interface ILocalPlayerTracker
{
    int? CurrentEntityId { get; }
    string? CurrentCharacterName { get; }

    event EventHandler<Exception>? OnError;
}
```

- [ ] **Step 5: Update `LocalPlayerTracker`**

Replace the full contents of `AlbionCompanion.Gathering/LocalPlayerTracker.cs`:

```csharp
using AlbionCompanion.Sniffer.AlbionEvents;
using AlbionCompanion.Sniffer.Protocol16;

namespace AlbionCompanion.Gathering;

// Broadcast events like HarvestStart are visible to every client in the zone, not just the
// local player's own actions (confirmed via live capture on 2026-07-16). GatheringEventRouter
// needs to filter those out by comparing an event's actor id against the local player's own
// current entity id - and, since 2026-08-03, GatheringSessionService needs the local player's
// current *character name* too, to attribute sessions to a Character.
//
// Two Photon signals feed this, both confirmed via live capture 2026-08-03:
//
// - The zone-join response (parameter 253 == 2) - already used for CurrentEntityId - is a
//   RESPONSE to our own REQUEST, so it is inherently self-only. It also carries the character's
//   nickname in parameter 2. This is the high-confidence source: both CurrentEntityId and
//   CurrentCharacterName are set from it with certainty.
// - PlayerAnnounce (semantic code 279) is a periodic EVENT that fires independent of zone
//   transitions - this is what lets CurrentEntityId recover after an app restart in the same
//   zone (the previously unfixed bug: no zone-join response means no signal at all otherwise).
//   It is NOT self-only (confirmed: two different nicknames observed for two different nearby
//   entities in one capture window), so a reading is only trusted as "us" when its nickname
//   matches either the name already confirmed via a zone-join this run (the common case - keeps
//   CurrentEntityId current as it churns), or any name in the user's registered character list
//   (the cold-start case - no zone-join has fired yet this run).
public class LocalPlayerTracker : ILocalPlayerTracker
{
    private const byte ZoneJoinSubCodeKey = 253;
    private const byte ZoneJoinSubCode = 2;
    private const byte ZoneJoinEntityIdParameterKey = 0;
    private const byte ZoneJoinNicknameParameterKey = 2;
    private const byte SemanticEventCodeParameterKey = 252;
    private const byte PlayerAnnounceEntityIdParameterKey = 0;
    private const byte PlayerAnnounceNicknameParameterKey = 2;

    private readonly ICharacterService _characterService;

    public int? CurrentEntityId { get; private set; }
    public string? CurrentCharacterName { get; private set; }

    public event EventHandler<Exception>? OnError;

    public LocalPlayerTracker(IPhotonParser photonParser, ICharacterService characterService)
    {
        _characterService = characterService;
        photonParser.OnResponseReceived += (_, response) => HandleResponse(response);
        photonParser.OnEventReceived += (_, e) => _ = HandleEventAsync(e);
    }

    internal void HandleResponse(PhotonResponse response)
    {
        if (!response.Parameters.TryGetValue(ZoneJoinSubCodeKey, out var subCode) ||
            Convert.ToInt32(subCode) != ZoneJoinSubCode)
        {
            return;
        }

        if (response.Parameters.TryGetValue(ZoneJoinEntityIdParameterKey, out var entityIdValue) && entityIdValue is not null)
        {
            CurrentEntityId = Convert.ToInt32(entityIdValue);
        }

        if (response.Parameters.TryGetValue(ZoneJoinNicknameParameterKey, out var nicknameValue) && nicknameValue is string nickname)
        {
            CurrentCharacterName = nickname;
        }
    }

    internal async Task HandleEventAsync(PhotonEvent photonEvent)
    {
        try
        {
            if (!photonEvent.Parameters.TryGetValue(SemanticEventCodeParameterKey, out var semanticCodeValue) ||
                semanticCodeValue is null)
            {
                return;
            }

            if (!TryToByte(semanticCodeValue, out var semanticCode) || semanticCode != (byte)AlbionEventCode.PlayerAnnounce)
            {
                return;
            }

            if (!photonEvent.Parameters.TryGetValue(PlayerAnnounceNicknameParameterKey, out var nicknameValue) ||
                nicknameValue is not string nickname)
            {
                return;
            }

            if (!photonEvent.Parameters.TryGetValue(PlayerAnnounceEntityIdParameterKey, out var entityIdValue) || entityIdValue is null)
            {
                return;
            }

            var isTrustedRefresh = CurrentCharacterName is not null && nickname == CurrentCharacterName;
            var isColdStartMatch = CurrentCharacterName is null &&
                (await _characterService.GetAllAsync()).Any(c => c.Name == nickname);

            if (!isTrustedRefresh && !isColdStartMatch)
            {
                return;
            }

            CurrentEntityId = Convert.ToInt32(entityIdValue);
            CurrentCharacterName = nickname;
        }
        catch (Exception ex)
        {
            OnError?.Invoke(this, ex);
        }
    }

    private static bool TryToByte(object value, out byte result)
    {
        var numeric = Convert.ToInt64(value);
        if (numeric is >= byte.MinValue and <= byte.MaxValue)
        {
            result = (byte)numeric;
            return true;
        }

        result = 0;
        return false;
    }
}
```

- [ ] **Step 6: Fix `GatheringEventRouterTests.cs`'s `FakeLocalPlayerTracker`**

In `AlbionCompanion.Gathering.Tests/GatheringEventRouterTests.cs`, replace:

```csharp
    private sealed class FakeLocalPlayerTracker : ILocalPlayerTracker
    {
        public int? CurrentEntityId { get; set; }
    }
```

with:

```csharp
    private sealed class FakeLocalPlayerTracker : ILocalPlayerTracker
    {
        public int? CurrentEntityId { get; set; }
        public string? CurrentCharacterName { get; set; }
        public event EventHandler<Exception>? OnError;
    }
```

- [ ] **Step 7: Run tests to verify they pass**

Run: `dotnet test AlbionCompanion.Gathering.Tests/AlbionCompanion.Gathering.Tests.csproj`
Expected: PASS, all tests (including the full existing suite, not just `LocalPlayerTrackerTests`) — `GatheringEventRouterTests` must still compile and pass since Step 6 only added members, not removed any.

- [ ] **Step 8: Commit**

```bash
git add AlbionCompanion.Sniffer/AlbionEvents/AlbionEventCode.cs AlbionCompanion.Gathering/ILocalPlayerTracker.cs AlbionCompanion.Gathering/LocalPlayerTracker.cs AlbionCompanion.Gathering.Tests/LocalPlayerTrackerTests.cs AlbionCompanion.Gathering.Tests/GatheringEventRouterTests.cs
git commit -m "feat(gathering): track current character name via zone-join + PlayerAnnounce broadcasts"
```

---

### Task 4: DI wiring — `ICharacterService` registration + `LocalPlayerTracker.OnError`

**Files:**
- Modify: `AlbionCompanion.Gathering/AppHostBuilder.cs`
- Modify: `AlbionCompanion.App/MauiProgram.cs`

**Interfaces:**
- Consumes: `ICharacterService`/`CharacterService` (Task 2), `ILocalPlayerTracker.OnError` (Task 3).
- Produces: `ICharacterService` resolvable both from the Gathering DI container and from Blazor's DI container (same pattern as `IItemDictionaryService`).

No new tests in this task — it's pure DI wiring, exercised end-to-end by the app itself. Verify with a build.

- [ ] **Step 1: Register `ICharacterService` and add its failure-log path**

In `AlbionCompanion.Gathering/AppHostBuilder.cs`, in `BuildServiceProvider`, add a new log path after `gatheringRouterFailuresLogPath`:

```csharp
        var gatheringRouterFailuresLogPath = Path.Combine(appDataPath, "debug_gathering_router_failures.log");
        var localPlayerTrackerFailuresLogPath = Path.Combine(appDataPath, "debug_local_player_tracker_failures.log");
        var dbPath = Path.Combine(appDataPath, "albion.db");
```

Add the service registration right before `services.AddSingleton<ILocalPlayerTracker, LocalPlayerTracker>();` (order matters here only for readability - DI resolves dependencies lazily regardless of registration order):

```csharp
        services.AddSingleton<ICharacterService, CharacterService>();
        services.AddSingleton<ILocalPlayerTracker, LocalPlayerTracker>();
```

Update the `HostLogPaths` registration call and record definition to carry the new path. Change:

```csharp
        services.AddSingleton(new HostLogPaths(parseFailuresLogPath, rawEventRecordFailuresLogPath, zoneTrackerFailuresLogPath, gatheringRouterFailuresLogPath));
```

to:

```csharp
        services.AddSingleton(new HostLogPaths(parseFailuresLogPath, rawEventRecordFailuresLogPath, zoneTrackerFailuresLogPath, gatheringRouterFailuresLogPath, localPlayerTrackerFailuresLogPath));
```

and change the record definition at the bottom of the file:

```csharp
    private sealed record HostLogPaths(string ParseFailuresLogPath, string RawEventRecordFailuresLogPath, string ZoneTrackerFailuresLogPath, string GatheringRouterFailuresLogPath, string LocalPlayerTrackerFailuresLogPath);
```

- [ ] **Step 2: Wire `LocalPlayerTracker.OnError` in `RunStartupSequenceAsync`**

In the same file, `RunStartupSequenceAsync` currently does:

```csharp
        _ = provider.GetRequiredService<AlbionEventLogger>();
        _ = provider.GetRequiredService<AlbionEventNameLogger>();
        _ = provider.GetRequiredService<ILocalPlayerTracker>();

        var sessionScope = provider.CreateScope();
        var zoneTracker = sessionScope.ServiceProvider.GetRequiredService<ZoneTracker>();
        var gatheringEventRouter = sessionScope.ServiceProvider.GetRequiredService<GatheringEventRouter>();

        var logPaths = provider.GetRequiredService<HostLogPaths>();
        zoneTracker.OnError += (_, ex) =>
            _ = File.AppendAllTextAsync(logPaths.ZoneTrackerFailuresLogPath, FormatFailureLine(ex));
```

Replace it with (moves `logPaths` up so it's available for the `LocalPlayerTracker` wiring, and captures the tracker as its concrete type to reach `OnError`):

```csharp
        var logPaths = provider.GetRequiredService<HostLogPaths>();

        _ = provider.GetRequiredService<AlbionEventLogger>();
        _ = provider.GetRequiredService<AlbionEventNameLogger>();
        var localPlayerTracker = (LocalPlayerTracker)provider.GetRequiredService<ILocalPlayerTracker>();
        localPlayerTracker.OnError += (_, ex) =>
            _ = File.AppendAllTextAsync(logPaths.LocalPlayerTrackerFailuresLogPath, FormatFailureLine(ex));

        var sessionScope = provider.CreateScope();
        var zoneTracker = sessionScope.ServiceProvider.GetRequiredService<ZoneTracker>();
        var gatheringEventRouter = sessionScope.ServiceProvider.GetRequiredService<GatheringEventRouter>();

        zoneTracker.OnError += (_, ex) =>
            _ = File.AppendAllTextAsync(logPaths.ZoneTrackerFailuresLogPath, FormatFailureLine(ex));
```

- [ ] **Step 3: Expose `ICharacterService` to Blazor's DI container**

In `AlbionCompanion.App/MauiProgram.cs`, add after the existing `IItemDictionaryService` registration:

```csharp
        builder.Services.AddSingleton<IItemDictionaryService>(_ =>
            GatheringProvider!.GetRequiredService<IItemDictionaryService>());
        builder.Services.AddSingleton<ICharacterService>(_ =>
            GatheringProvider!.GetRequiredService<ICharacterService>());
```

- [ ] **Step 4: Build to verify**

Run: `dotnet build AlbionCompanion.App/AlbionCompanion.App.csproj -f net10.0-windows10.0.19041.0 -r win-x64`
Expected: succeeds with 0 errors. (If it fails with `MSB3027`/file-lock, the running app needs to be closed first - see Global Constraints.)

- [ ] **Step 5: Run the full Gathering test suite**

Run: `dotnet test AlbionCompanion.Gathering.Tests/AlbionCompanion.Gathering.Tests.csproj`
Expected: PASS, unaffected by this task's changes (pure DI wiring, no test-visible surface changed).

- [ ] **Step 6: Commit**

```bash
git add AlbionCompanion.Gathering/AppHostBuilder.cs AlbionCompanion.App/MauiProgram.cs
git commit -m "feat(gathering): wire up ICharacterService DI and LocalPlayerTracker failure logging"
```

---

### Task 5: `GatheringSessionService` resolves `CharacterId` when a session starts

**Files:**
- Modify: `AlbionCompanion.Gathering/IGatheringSessionService.cs`
- Modify: `AlbionCompanion.Gathering/GatheringSessionService.cs`
- Test: `AlbionCompanion.Gathering.Tests/GatheringSessionServiceTests.cs`
- Test (fix existing helper): `AlbionCompanion.Gathering.Tests/GatheringEventRouterTests.cs`

**Interfaces:**
- Consumes: `ILocalPlayerTracker.CurrentCharacterName` (Task 3), `ICharacterService.GetAllAsync()` (Task 2).
- Produces: `GatheringSession.CharacterId` populated on creation; `ActiveSessionSnapshot.CharacterId` (`Guid?`, new field, inserted right after `CurrentLocation`).

- [ ] **Step 1: Write the failing tests**

In `AlbionCompanion.Gathering.Tests/GatheringSessionServiceTests.cs`, add these two fakes and a `CreateService` helper right after the `CreateInMemoryContext` method:

```csharp
    private sealed class FakeLocalPlayerTracker : ILocalPlayerTracker
    {
        public int? CurrentEntityId { get; set; }
        public string? CurrentCharacterName { get; set; }
        public event EventHandler<Exception>? OnError;
    }

    private sealed class FakeCharacterService : ICharacterService
    {
        private readonly List<Character> _characters = new();

        public Task<IReadOnlyList<Character>> GetAllAsync() => Task.FromResult<IReadOnlyList<Character>>(_characters);

        public Task<Character> AddAsync(string name)
        {
            var character = new Character { Name = name, CreatedAt = DateTime.UtcNow };
            _characters.Add(character);
            return Task.FromResult(character);
        }

        public Task DeleteAsync(Guid id) => throw new NotImplementedException();
        public Task<IReadOnlyList<CharacterOverview>> GetAllOverviewsAsync() => throw new NotImplementedException();
        public Task<CharacterOverview?> GetOverviewAsync(Guid characterId) => throw new NotImplementedException();
    }

    private static GatheringSessionService CreateService(
        AppDbContext context, ILocalPlayerTracker? localPlayerTracker = null, ICharacterService? characterService = null) =>
        new(context, localPlayerTracker ?? new FakeLocalPlayerTracker(), characterService ?? new FakeCharacterService());
```

Now replace every `new GatheringSessionService(context)` call in the file with `CreateService(context)` — there are 20 occurrences, all identical text, so a single find-and-replace-all covers them.

Then add these three new tests at the end of the class, right before the closing `}`:

```csharp
    [Fact]
    public async Task StartSessionAsync_WithKnownCharacterName_ResolvesCharacterId()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        using var context = CreateInMemoryContext(connection);
        var characterService = new FakeCharacterService();
        var character = await characterService.AddAsync("Ejnsztain");
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
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test AlbionCompanion.Gathering.Tests/AlbionCompanion.Gathering.Tests.csproj --filter GatheringSessionServiceTests`
Expected: FAIL to compile — `GatheringSessionService`'s constructor doesn't take the new dependencies yet, `active.CharacterId` doesn't exist.

- [ ] **Step 3: Update `IGatheringSessionService.cs`**

In `AlbionCompanion.Gathering/IGatheringSessionService.cs`, change the `ActiveSessionSnapshot` record from:

```csharp
public record ActiveSessionSnapshot(
    string CurrentLocation,
    int TotalFameEarned,
    int TotalSilverEarned,
    IReadOnlyList<ItemLocationTotal> ItemTotals,
    IReadOnlyList<LocationTotal> FameByLocation,
    IReadOnlyList<LocationTotal> SilverByLocation);
```

to:

```csharp
public record ActiveSessionSnapshot(
    string CurrentLocation,
    Guid? CharacterId,
    int TotalFameEarned,
    int TotalSilverEarned,
    IReadOnlyList<ItemLocationTotal> ItemTotals,
    IReadOnlyList<LocationTotal> FameByLocation,
    IReadOnlyList<LocationTotal> SilverByLocation);
```

- [ ] **Step 4: Update `GatheringSessionService.cs`**

Change the constructor from:

```csharp
    private readonly AppDbContext _dbContext;

    public GatheringSessionService(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }
```

to:

```csharp
    private readonly AppDbContext _dbContext;
    private readonly ILocalPlayerTracker _localPlayerTracker;
    private readonly ICharacterService _characterService;

    public GatheringSessionService(AppDbContext dbContext, ILocalPlayerTracker localPlayerTracker, ICharacterService characterService)
    {
        _dbContext = dbContext;
        _localPlayerTracker = localPlayerTracker;
        _characterService = characterService;
    }
```

In `GetActiveSessionSnapshotAsync`, change the final `return` from:

```csharp
        return new ActiveSessionSnapshot(
            session.CurrentLocation,
            session.TotalFameEarned,
            session.TotalSilverEarned,
            itemTotals,
            fameByLocation,
            silverByLocation);
```

to:

```csharp
        return new ActiveSessionSnapshot(
            session.CurrentLocation,
            session.CharacterId,
            session.TotalFameEarned,
            session.TotalSilverEarned,
            itemTotals,
            fameByLocation,
            silverByLocation);
```

In `StartSessionAsync`, change the new-session branch from:

```csharp
        var session = new GatheringSession
        {
            StartTime = DateTime.UtcNow,
            StartLocation = location,
            CurrentLocation = location,
        };
        _dbContext.GatheringSessions.Add(session);
```

to:

```csharp
        var session = new GatheringSession
        {
            StartTime = DateTime.UtcNow,
            StartLocation = location,
            CurrentLocation = location,
            CharacterId = await ResolveCharacterIdAsync(),
        };
        _dbContext.GatheringSessions.Add(session);
```

Add a new private method at the bottom of the class, right before the closing `}`:

```csharp
    private async Task<Guid?> ResolveCharacterIdAsync()
    {
        if (_localPlayerTracker.CurrentCharacterName is not { } name)
        {
            return null;
        }

        var characters = await _characterService.GetAllAsync();
        return characters.FirstOrDefault(c => c.Name == name)?.Id;
    }
```

- [ ] **Step 5: Fix `GatheringEventRouterTests.cs`'s `CreateServiceWithOpenSession` helper**

In `AlbionCompanion.Gathering.Tests/GatheringEventRouterTests.cs`, this file already has its own `FakeLocalPlayerTracker` (used for `GatheringEventRouter`, extended in Task 3 with `CurrentCharacterName`/`OnError`) — add a small `FakeCharacterService` right after it:

```csharp
    private sealed class FakeCharacterService : ICharacterService
    {
        public Task<IReadOnlyList<Character>> GetAllAsync() => Task.FromResult<IReadOnlyList<Character>>(Array.Empty<Character>());
        public Task<Character> AddAsync(string name) => throw new NotImplementedException();
        public Task DeleteAsync(Guid id) => throw new NotImplementedException();
        public Task<IReadOnlyList<CharacterOverview>> GetAllOverviewsAsync() => throw new NotImplementedException();
        public Task<CharacterOverview?> GetOverviewAsync(Guid characterId) => throw new NotImplementedException();
    }
```

Then change `CreateServiceWithOpenSession` from:

```csharp
    private static (GatheringSessionService Service, AppDbContext Context) CreateServiceWithOpenSession(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        var context = new AppDbContext(options);
        context.Database.EnsureCreated();
        var service = new GatheringSessionService(context);
        return (service, context);
    }
```

to:

```csharp
    private static (GatheringSessionService Service, AppDbContext Context) CreateServiceWithOpenSession(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        var context = new AppDbContext(options);
        context.Database.EnsureCreated();
        var service = new GatheringSessionService(context, new FakeLocalPlayerTracker(), new FakeCharacterService());
        return (service, context);
    }
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test AlbionCompanion.Gathering.Tests/AlbionCompanion.Gathering.Tests.csproj`
Expected: PASS, full suite green.

- [ ] **Step 7: Commit**

```bash
git add AlbionCompanion.Gathering/IGatheringSessionService.cs AlbionCompanion.Gathering/GatheringSessionService.cs AlbionCompanion.Gathering.Tests/GatheringSessionServiceTests.cs AlbionCompanion.Gathering.Tests/GatheringEventRouterTests.cs
git commit -m "feat(gathering): resolve CharacterId when a new session starts"
```

---

### Task 6: `IGatheringLiveState` exposes `CharacterId` and a session-started event for the UI

**Files:**
- Modify: `AlbionCompanion.Gathering/IGatheringLiveState.cs`
- Modify: `AlbionCompanion.Gathering/GatheringLiveState.cs`
- Test: `AlbionCompanion.Gathering.Tests/GatheringLiveStateTests.cs`

**Interfaces:**
- Consumes: `ActiveSessionSnapshot.CharacterId` (Task 5), `GatheringSession.CharacterId` (Task 1).
- Produces: `IGatheringLiveState.CharacterId` (`Guid?`), `IGatheringLiveState.OnSessionStarted` (`EventHandler<GatheringSession>?`) — a Blazor-safe passthrough of `IGatheringSessionService.OnSessionStarted`, used later by the toast notification (Task 11) without any DI-scope-timing risk.

- [ ] **Step 1: Write the failing tests**

In `AlbionCompanion.Gathering.Tests/GatheringLiveStateTests.cs`, the two existing `new ActiveSessionSnapshot(...)` calls need a `CharacterId` argument. Both occurrences currently read:

```csharp
            SnapshotToReturn = new ActiveSessionSnapshot(
                CurrentLocation: "Cairn Camain",
                TotalFameEarned: 150,
                TotalSilverEarned: 500,
                ItemTotals: new[] { new ItemLocationTotal("T4_ORE", "Cairn Camain", 12) },
                FameByLocation: new[] { new LocationTotal("Cairn Camain", 150) },
                SilverByLocation: new[] { new LocationTotal("Cairn Camain", 500) }),
```

Replace both occurrences (find-and-replace-all, identical text) with:

```csharp
            SnapshotToReturn = new ActiveSessionSnapshot(
                CurrentLocation: "Cairn Camain",
                CharacterId: TestCharacterId,
                TotalFameEarned: 150,
                TotalSilverEarned: 500,
                ItemTotals: new[] { new ItemLocationTotal("T4_ORE", "Cairn Camain", 12) },
                FameByLocation: new[] { new LocationTotal("Cairn Camain", 150) },
                SilverByLocation: new[] { new LocationTotal("Cairn Camain", 500) }),
```

Add a `TestCharacterId` constant near the top of the class, right after the `AmountFor` helper method:

```csharp
    private static readonly Guid TestCharacterId = Guid.Parse("11111111-1111-1111-1111-111111111111");
```

Add these three new tests at the end of the class, right before the closing `}`:

```csharp
    [Fact]
    public async Task Attach_WithAlreadyActiveSession_RehydratesCharacterId()
    {
        var liveState = new GatheringLiveState();
        var service = new FakeGatheringSessionService
        {
            SnapshotToReturn = new ActiveSessionSnapshot(
                CurrentLocation: "Cairn Camain",
                CharacterId: TestCharacterId,
                TotalFameEarned: 150,
                TotalSilverEarned: 500,
                ItemTotals: Array.Empty<ItemLocationTotal>(),
                FameByLocation: Array.Empty<LocationTotal>(),
                SilverByLocation: Array.Empty<LocationTotal>()),
        };

        await liveState.Attach(service);

        Assert.Equal(TestCharacterId, liveState.CharacterId);
    }

    [Fact]
    public async Task OnSessionStarted_SetsCharacterId()
    {
        var liveState = new GatheringLiveState();
        var service = new FakeGatheringSessionService();
        await liveState.Attach(service);

        service.RaiseSessionStarted(new GatheringSession { StartLocation = "Martlock", CharacterId = TestCharacterId });

        Assert.Equal(TestCharacterId, liveState.CharacterId);
    }

    [Fact]
    public async Task OnSessionStarted_RaisesPassthroughEventWithTheNewSession()
    {
        var liveState = new GatheringLiveState();
        var service = new FakeGatheringSessionService();
        await liveState.Attach(service);
        GatheringSession? raised = null;
        liveState.OnSessionStarted += (_, session) => raised = session;

        service.RaiseSessionStarted(new GatheringSession { StartLocation = "Martlock", CharacterId = TestCharacterId });

        Assert.NotNull(raised);
        Assert.Equal(TestCharacterId, raised!.CharacterId);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test AlbionCompanion.Gathering.Tests/AlbionCompanion.Gathering.Tests.csproj --filter GatheringLiveStateTests`
Expected: FAIL to compile — `IGatheringLiveState`/`GatheringLiveState` don't have `CharacterId` or `OnSessionStarted` yet.

- [ ] **Step 3: Update `IGatheringLiveState.cs`**

Replace the full contents of `AlbionCompanion.Gathering/IGatheringLiveState.cs`:

```csharp
using AlbionCompanion.Core.Models;

namespace AlbionCompanion.Gathering;

public interface IGatheringLiveState
{
    bool IsActive { get; }
    string? StartLocation { get; }
    string? CurrentLocation { get; }
    Guid? CharacterId { get; }
    int TotalFame { get; }
    int TotalSilver { get; }
    IReadOnlyList<ItemLocationTotal> ItemTotals { get; }
    IReadOnlyList<LocationTotal> FameByLocation { get; }
    IReadOnlyList<LocationTotal> SilverByLocation { get; }

    event EventHandler? OnChanged;
    // Passthrough of IGatheringSessionService.OnSessionStarted's own event, re-raised from this
    // already-DI-safe Blazor singleton - lets UI components (the session-start toast) subscribe
    // without injecting IGatheringSessionService directly, which lives in a scope that isn't
    // guaranteed ready by the time Blazor components start resolving DI (see App.xaml.cs's
    // fire-and-forget StartGatheringAsync).
    event EventHandler<GatheringSession>? OnSessionStarted;

    Task Attach(IGatheringSessionService sessionService);
}
```

- [ ] **Step 4: Update `GatheringLiveState.cs`**

Add the new field and property. Change:

```csharp
    private bool _isActive;
    private string? _startLocation;
    private string? _currentLocation;
    private int _totalFame;
    private int _totalSilver;
```

to:

```csharp
    private bool _isActive;
    private string? _startLocation;
    private string? _currentLocation;
    private Guid? _characterId;
    private int _totalFame;
    private int _totalSilver;
```

Add the property right after `CurrentLocation`'s getter:

```csharp
    public Guid? CharacterId
    {
        get { lock (_lock) { return _characterId; } }
    }
```

Add the new event declaration right after `public event EventHandler? OnChanged;`:

```csharp
    public event EventHandler? OnChanged;
    public event EventHandler<GatheringSession>? OnSessionStarted;
```

In `Attach`, update the snapshot-rehydration block. Change:

```csharp
                _isActive = true;
                _startLocation = snapshot.CurrentLocation;
                _currentLocation = snapshot.CurrentLocation;
                _totalFame = snapshot.TotalFameEarned;
```

to:

```csharp
                _isActive = true;
                _startLocation = snapshot.CurrentLocation;
                _currentLocation = snapshot.CurrentLocation;
                _characterId = snapshot.CharacterId;
                _totalFame = snapshot.TotalFameEarned;
```

Update the `OnSessionStarted` handler. Change:

```csharp
        sessionService.OnSessionStarted += (_, session) => Safely(() =>
        {
            lock (_lock)
            {
                _itemTotals.Clear();
                _fameByLocation.Clear();
                _silverByLocation.Clear();
                _totalFame = 0;
                _totalSilver = 0;
                _startLocation = session.StartLocation;
                _currentLocation = session.StartLocation;
                _isActive = true;
            }
        });
```

to:

```csharp
        sessionService.OnSessionStarted += (_, session) => Safely(() =>
        {
            lock (_lock)
            {
                _itemTotals.Clear();
                _fameByLocation.Clear();
                _silverByLocation.Clear();
                _totalFame = 0;
                _totalSilver = 0;
                _startLocation = session.StartLocation;
                _currentLocation = session.StartLocation;
                _characterId = session.CharacterId;
                _isActive = true;
            }
        });

        sessionService.OnSessionStarted += (_, session) =>
        {
            try
            {
                OnSessionStarted?.Invoke(this, session);
            }
            catch
            {
                // Same boundary rule as Safely() below - a failing UI subscriber must never
                // destabilize the gathering pipeline this event also drives.
            }
        };
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test AlbionCompanion.Gathering.Tests/AlbionCompanion.Gathering.Tests.csproj`
Expected: PASS, full suite green.

- [ ] **Step 6: Commit**

```bash
git add AlbionCompanion.Gathering/IGatheringLiveState.cs AlbionCompanion.Gathering/GatheringLiveState.cs AlbionCompanion.Gathering.Tests/GatheringLiveStateTests.cs
git commit -m "feat(gathering): expose CharacterId and an OnSessionStarted passthrough on IGatheringLiveState"
```

---

### Task 7: `SessionHistoryService` — `CharacterName` on session rows + per-character session filter

**Files:**
- Modify: `AlbionCompanion.Gathering/ISessionHistoryService.cs`
- Modify: `AlbionCompanion.Gathering/SessionHistoryService.cs`
- Test: `AlbionCompanion.Gathering.Tests/SessionHistoryServiceTests.cs`

**Interfaces:**
- Consumes: `GatheringSession.Character` (Task 1).
- Produces: `SessionSummary.CharacterName` (`string`, "Unknown" when the session has no `Character`), `SessionQuery.CharacterId` (`Guid?`, optional filter — used by the character dashboard in Task 9).

- [ ] **Step 1: Write the failing tests**

In `AlbionCompanion.Gathering.Tests/SessionHistoryServiceTests.cs`, add these two tests at the end of the class, right before the closing `}`:

```csharp
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
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test AlbionCompanion.Gathering.Tests/AlbionCompanion.Gathering.Tests.csproj --filter SessionHistoryServiceTests`
Expected: FAIL to compile — `SessionQuery` has no `CharacterId`, `SessionSummary` has no `CharacterName`.

- [ ] **Step 3: Update `ISessionHistoryService.cs`**

Change `SessionQuery` from:

```csharp
public record SessionQuery(
    int Page = 1,
    int PageSize = 20,
    string? LocationFilter = null,
    SessionSortColumn SortBy = SessionSortColumn.StartTime,
    bool SortDescending = true);
```

to:

```csharp
public record SessionQuery(
    int Page = 1,
    int PageSize = 20,
    string? LocationFilter = null,
    Guid? CharacterId = null,
    SessionSortColumn SortBy = SessionSortColumn.StartTime,
    bool SortDescending = true);
```

Change `SessionSummary` from:

```csharp
public record SessionSummary(
    Guid Id,
    DateTime StartTime,
    DateTime EndTime,
    string StartLocation,
    IReadOnlyList<string> Locations,
    int TotalFameEarned,
    int TotalItemsCollected);
```

to:

```csharp
public record SessionSummary(
    Guid Id,
    DateTime StartTime,
    DateTime EndTime,
    string StartLocation,
    IReadOnlyList<string> Locations,
    int TotalFameEarned,
    int TotalItemsCollected,
    string CharacterName);
```

- [ ] **Step 4: Update `SessionHistoryService.cs`**

In `GetCompletedSessionsAsync`, add the character filter right after the existing location filter. Change:

```csharp
        var filtered = dbContext.GatheringSessions.Where(s => s.EndTime != null);

        if (!string.IsNullOrWhiteSpace(query.LocationFilter))
        {
            filtered = filtered.Where(s => EF.Functions.Like(s.StartLocation, $"%{query.LocationFilter}%"));
        }
```

to:

```csharp
        var filtered = dbContext.GatheringSessions.Where(s => s.EndTime != null);

        if (query.CharacterId is { } characterId)
        {
            filtered = filtered.Where(s => s.CharacterId == characterId);
        }

        if (!string.IsNullOrWhiteSpace(query.LocationFilter))
        {
            filtered = filtered.Where(s => EF.Functions.Like(s.StartLocation, $"%{query.LocationFilter}%"));
        }
```

Add `.Include(s => s.Character)` to the existing `Include` chain. Change:

```csharp
        var pageEntities = await sorted
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Include(s => s.GatheredItems)
            .Include(s => s.FameLogs)
            .Include(s => s.SilverLogs)
            .ToListAsync();
```

to:

```csharp
        var pageEntities = await sorted
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Include(s => s.GatheredItems)
            .Include(s => s.FameLogs)
            .Include(s => s.SilverLogs)
            .Include(s => s.Character)
            .ToListAsync();
```

Add `CharacterName` to the projection. Change:

```csharp
        var items = pageEntities
            .Select(s => new SessionSummary(
                s.Id,
                s.StartTime,
                s.EndTime!.Value,
                s.StartLocation,
                LocationsVisited(
                    s.StartLocation,
                    s.GatheredItems.Select(i => i.Location),
                    s.FameLogs.Select(f => f.Location),
                    s.SilverLogs.Select(silver => silver.Location)),
                s.TotalFameEarned,
                s.GatheredItems.Sum(i => i.Amount)))
            .ToList();
```

to:

```csharp
        var items = pageEntities
            .Select(s => new SessionSummary(
                s.Id,
                s.StartTime,
                s.EndTime!.Value,
                s.StartLocation,
                LocationsVisited(
                    s.StartLocation,
                    s.GatheredItems.Select(i => i.Location),
                    s.FameLogs.Select(f => f.Location),
                    s.SilverLogs.Select(silver => silver.Location)),
                s.TotalFameEarned,
                s.GatheredItems.Sum(i => i.Amount),
                s.Character?.Name ?? "Unknown"))
            .ToList();
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test AlbionCompanion.Gathering.Tests/AlbionCompanion.Gathering.Tests.csproj`
Expected: PASS, full suite green.

- [ ] **Step 6: Commit**

```bash
git add AlbionCompanion.Gathering/ISessionHistoryService.cs AlbionCompanion.Gathering/SessionHistoryService.cs AlbionCompanion.Gathering.Tests/SessionHistoryServiceTests.cs
git commit -m "feat(gathering): add CharacterName to session summaries and a per-character session filter"
```

---

### Task 8: Character hub page (new `/`) with add-character form

**Files:**
- Create: `AlbionCompanion.App/Components/Pages/CharacterHub.razor`
- Delete: `AlbionCompanion.App/Components/Pages/Home.razor` (its content moves to `Broadcast.razor` in Task 10 — deleting here just frees up the `@page "/"` route for this task; if Task 10 hasn't run yet in your working copy, hold off deleting until Task 10's first step recreates the content elsewhere)
- Modify: `AlbionCompanion.App/Components/Layout/NavMenu.razor`
- Modify: `AlbionCompanion.App/wwwroot/app.css`

**Interfaces:**
- Consumes: `ICharacterService.GetAllOverviewsAsync()`/`.AddAsync(string)` (Task 2), exposed to Blazor DI (Task 4).

This task is UI-only — no unit tests (this codebase's existing Razor pages aren't unit-tested either; verified by building and by the user's manual pass). Build after every step.

- [ ] **Step 1: Add hub/toast/character-card CSS**

In `AlbionCompanion.App/wwwroot/app.css`, add after the `.ac-card-sm`/`.ac-card-stats` block (right after the existing `.ac-card-row .ac-card { margin-bottom: 0; }` rule and its neighbors — anywhere in the `/* ---- fame card ---- */` section is fine):

```css
.ac-character-card {
    cursor: pointer;
    transition: box-shadow 0.15s ease;
}

.ac-character-card:hover {
    box-shadow: 0 0 0 1px var(--ac-accent);
}

.ac-character-card-active {
    box-shadow: 0 0 0 1px var(--ac-active);
}

.ac-tag-active {
    display: inline-flex;
    align-items: center;
    gap: 6px;
    font-size: 11px;
    letter-spacing: 0.04em;
    text-transform: uppercase;
    color: var(--ac-active);
}

.ac-add-character-card {
    display: flex;
    align-items: center;
    justify-content: center;
    border: 1px dashed var(--ac-divider);
    background: transparent;
    box-shadow: none;
    min-height: 140px;
}

/* ---- toast ---- */
.ac-toast {
    position: fixed;
    bottom: var(--ac-space-6);
    right: var(--ac-space-6);
    display: flex;
    align-items: center;
    gap: var(--ac-space-3);
    background: var(--ac-surface-raised);
    border: 1px solid var(--ac-divider);
    border-radius: var(--ac-radius-md);
    box-shadow: 0 4px 24px rgba(0, 0, 0, 0.3);
    padding: var(--ac-space-3) var(--ac-space-4);
    color: var(--ac-text);
    font-size: 13px;
    z-index: 1000;
}

.ac-toast-link {
    color: var(--ac-accent);
    font-weight: 600;
    text-decoration: none;
}

.ac-toast-close {
    background: none;
    border: none;
    color: var(--ac-text-muted);
    cursor: pointer;
    font-size: 14px;
    padding: 0;
}
```

- [ ] **Step 2: Write `CharacterHub.razor`**

Write `AlbionCompanion.App/Components/Pages/CharacterHub.razor`:

```razor
@page "/"
@using AlbionCompanion.Gathering
@using Microsoft.EntityFrameworkCore
@inject ICharacterService CharacterService
@inject NavigationManager Navigation

<h1 style="font-size: 24px; margin-bottom: 4px;">Characters</h1>
<p class="ac-subtitle">Pick a character to review its history, or wait for a session to start.</p>

@if (_loadFailed)
{
    <p class="ac-subtitle">Could not load characters yet — the database may still be initializing. Try again shortly.</p>
}
else if (_overviews is null)
{
    <p class="ac-subtitle">Loading...</p>
}
else
{
    <div class="ac-card-row">
        @foreach (var overview in _overviews)
        {
            <div class="ac-card ac-character-card @(overview.HasActiveSession ? "ac-character-card-active" : "")"
                 @onclick="() => Navigation.NavigateTo($"/characters/{overview.Id}")">
                <div class="ac-card-head">
                    <div class="ac-card-kicker">@overview.Name</div>
                    @if (overview.HasActiveSession)
                    {
                        <span class="ac-tag-active">
                            <span class="ac-dot ac-dot-active"></span>
                            Live
                        </span>
                    }
                </div>
                <p class="ac-subtitle" style="margin: 0 0 var(--ac-space-3);">
                    @(overview.LastActive is { } lastActive ? $"Last active {lastActive.ToLocalTime():g}" : "Never played")
                </p>
                <div class="ac-card-stats">
                    <div class="ac-card-stat">
                        <div class="ac-card-stat-label">Fame</div>
                        <div class="ac-card-value">@overview.TotalFameEarned.ToString("N0")</div>
                    </div>
                    <div class="ac-card-stat">
                        <div class="ac-card-stat-label">Silver</div>
                        <div class="ac-card-value">@overview.TotalSilverEarned.ToString("N0")</div>
                    </div>
                    <div class="ac-card-stat">
                        <div class="ac-card-stat-label">Items</div>
                        <div class="ac-card-value">@overview.TotalItemsCollected.ToString("N0")</div>
                    </div>
                </div>
            </div>
        }

        <div class="ac-card ac-add-character-card">
            @if (_showAddForm)
            {
                <div style="width:100%;">
                    <div class="ac-card-kicker" style="margin-bottom: var(--ac-space-3);">New character</div>
                    <input class="ac-input" style="width: 100%; margin-bottom: var(--ac-space-3);" placeholder="Exact in-game name"
                           value="@_newCharacterName" @oninput="e => _newCharacterName = e.Value?.ToString() ?? string.Empty" />
                    @if (_addError is not null)
                    {
                        <p class="ac-subtitle" style="color: var(--ac-ended);">@_addError</p>
                    }
                    <div style="display:flex; gap: var(--ac-space-2);">
                        <button class="ac-pager-btn" @onclick="AddCharacterAsync">Add</button>
                        <button class="ac-pager-btn" @onclick="() => _showAddForm = false">Cancel</button>
                    </div>
                </div>
            }
            else
            {
                <button class="ac-pager-btn" style="width: 100%; height: 100%;" @onclick="() => _showAddForm = true">+ Add character</button>
            }
        </div>
    </div>
}

@code {
    private IReadOnlyList<CharacterOverview>? _overviews;
    private bool _loadFailed;
    private bool _showAddForm;
    private string _newCharacterName = string.Empty;
    private string? _addError;

    protected override async Task OnInitializedAsync() => await LoadAsync();

    private async Task LoadAsync()
    {
        try
        {
            _overviews = await CharacterService.GetAllOverviewsAsync();
        }
        catch
        {
            // Only expected on a first-ever launch, if the user navigates here before
            // AppHostBuilder.RunStartupSequenceAsync's migration (running concurrently on a
            // separate DbContext) has created the schema. Degrade to a retry message instead of
            // an unhandled Blazor error.
            _loadFailed = true;
        }
    }

    private async Task AddCharacterAsync()
    {
        var name = _newCharacterName.Trim();
        if (name.Length == 0)
        {
            _addError = "Enter a name.";
            return;
        }

        try
        {
            await CharacterService.AddAsync(name);
            _newCharacterName = string.Empty;
            _addError = null;
            _showAddForm = false;
            await LoadAsync();
        }
        catch (DbUpdateException)
        {
            _addError = $"\"{name}\" is already registered.";
        }
    }
}
```

- [ ] **Step 3: Update `NavMenu.razor`**

The nav's first item currently points to the live-session view, which this task is replacing as the app's landing page. Change:

```razor
<NavLink class="ac-navlink" href="" Match="NavLinkMatch.All">
    <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M15 21v-8a1 1 0 0 0-1-1h-4a1 1 0 0 0-1 1v8" /><path d="M3 10a2 2 0 0 1 .709-1.528l7-5.999a2 2 0 0 1 2.582 0l7 5.999A2 2 0 0 1 21 10v9a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z" /></svg>
    Home
</NavLink>
```

to:

```razor
<NavLink class="ac-navlink" href="" Match="NavLinkMatch.All">
    <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M17 21v-2a4 4 0 0 0-4-4H5a4 4 0 0 0-4 4v2" /><circle cx="9" cy="7" r="4" /><path d="M23 21v-2a4 4 0 0 0-3-3.87" /><path d="M16 3.13a4 4 0 0 1 0 7.75" /></svg>
    Characters
</NavLink>
```

- [ ] **Step 4: Delete the old `Home.razor`**

`Home.razor` currently owns the `@page "/"` route this task's `CharacterHub.razor` now claims — two pages can't share a route. Delete `AlbionCompanion.App/Components/Pages/Home.razor` now; its content is recreated (with character-scoping) as `Broadcast.razor` in Task 10. The app will build and run correctly between this task and Task 10 — there just won't be a live-session view until Task 10 adds it back.

- [ ] **Step 5: Build to verify**

Run: `dotnet build AlbionCompanion.App/AlbionCompanion.App.csproj -f net10.0-windows10.0.19041.0 -r win-x64`
Expected: succeeds with 0 errors, 0 warnings.

- [ ] **Step 6: Commit**

```bash
git add AlbionCompanion.App/Components/Pages/CharacterHub.razor AlbionCompanion.App/Components/Layout/NavMenu.razor AlbionCompanion.App/wwwroot/app.css
git rm AlbionCompanion.App/Components/Pages/Home.razor
git commit -m "feat(app): add character hub as the new landing page"
```

---

### Task 9: Character dashboard page (`/characters/{id}`)

**Files:**
- Create: `AlbionCompanion.App/Components/Pages/CharacterDashboard.razor`

**Interfaces:**
- Consumes: `ICharacterService.GetOverviewAsync(Guid)` (Task 2), `ISessionHistoryService.GetCompletedSessionsAsync(SessionQuery)` with `CharacterId` (Task 7).

- [ ] **Step 1: Write `CharacterDashboard.razor`**

Write `AlbionCompanion.App/Components/Pages/CharacterDashboard.razor`:

```razor
@page "/characters/{CharacterId:guid}"
@using AlbionCompanion.Gathering
@inject ICharacterService CharacterService
@inject ISessionHistoryService HistoryService
@inject NavigationManager Navigation

<a class="ac-back-link" href="/">
    <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="m12 19-7-7 7-7" /><path d="M19 12H5" /></svg>
    Characters
</a>

@if (_loadFailed)
{
    <p class="ac-subtitle">Could not load this character yet — the database may still be initializing. Try again shortly.</p>
}
else if (!_hasLoaded)
{
    <p class="ac-subtitle">Loading...</p>
}
else if (_overview is null)
{
    <p class="ac-subtitle">Character not found.</p>
}
else
{
    <h1 style="font-size: 24px; margin-bottom: 4px;">@_overview.Name</h1>
    @if (_overview.HasActiveSession)
    {
        <p class="ac-subtitle">
            <span class="ac-dot ac-dot-active" style="display:inline-block; margin-right:6px;"></span>
            Currently active — <a href="@($"/characters/{CharacterId}/broadcast")">watch the live broadcast</a>
        </p>
    }
    else
    {
        <p class="ac-subtitle">@(_overview.LastActive is { } lastActive ? $"Last active {lastActive.ToLocalTime():g}" : "Never played")</p>
    }

    <div class="ac-card-row">
        <div class="ac-card">
            <div class="ac-card-head">
                <div class="ac-card-kicker">All-time</div>
            </div>
            <div class="ac-card-stats">
                <div class="ac-card-stat">
                    <div class="ac-card-stat-label">Fame</div>
                    <div class="ac-card-value">@_overview.TotalFameEarned.ToString("N0")</div>
                </div>
                <div class="ac-card-stat">
                    <div class="ac-card-stat-label">Silver</div>
                    <div class="ac-card-value">@_overview.TotalSilverEarned.ToString("N0")</div>
                </div>
                <div class="ac-card-stat">
                    <div class="ac-card-stat-label">Items</div>
                    <div class="ac-card-value">@_overview.TotalItemsCollected.ToString("N0")</div>
                </div>
            </div>
        </div>
    </div>

    <div class="ac-section-label">Sessions</div>

    @if (_page is null)
    {
        <p class="ac-subtitle">Loading sessions...</p>
    }
    else if (_page.TotalCount == 0)
    {
        <p class="ac-subtitle">No completed sessions yet.</p>
    }
    else
    {
        <table class="ac-table">
            <thead>
                <tr>
                    <th>Zone</th>
                    <th>Start</th>
                    <th>Duration</th>
                    <th class="ac-num">Fame</th>
                    <th class="ac-num">Items</th>
                </tr>
            </thead>
            <tbody>
                @foreach (var session in _page.Items)
                {
                    <tr class="ac-row-clickable" @onclick="() => Navigation.NavigateTo($"/sessions/{session.Id}")">
                        <td class="ac-item-cell">@string.Join(", ", session.Locations)</td>
                        <td>@session.StartTime.ToLocalTime().ToString("g")</td>
                        <td>@FormatDuration(session.EndTime - session.StartTime)</td>
                        <td class="ac-num">@session.TotalFameEarned.ToString("N0")</td>
                        <td class="ac-num">@session.TotalItemsCollected</td>
                    </tr>
                }
            </tbody>
        </table>

        <div class="ac-pager">
            <button class="ac-pager-btn" disabled="@(_page.Page <= 1)" @onclick="() => GoToPage(_page.Page - 1)">Prev</button>
            <span class="ac-subtitle" style="margin:0;">Page @_page.Page of @_page.TotalPages</span>
            <button class="ac-pager-btn" disabled="@(_page.Page >= _page.TotalPages)" @onclick="() => GoToPage(_page.Page + 1)">Next</button>
        </div>
    }
}

@code {
    [Parameter]
    public Guid CharacterId { get; set; }

    private CharacterOverview? _overview;
    private PagedResult<SessionSummary>? _page;
    private bool _loadFailed;
    private bool _hasLoaded;
    private int _currentPage = 1;

    protected override async Task OnParametersSetAsync()
    {
        try
        {
            _overview = await CharacterService.GetOverviewAsync(CharacterId);
            if (_overview is not null)
            {
                await LoadSessionsAsync();
            }
        }
        catch
        {
            _loadFailed = true;
        }
        finally
        {
            _hasLoaded = true;
        }
    }

    private async Task LoadSessionsAsync()
    {
        _page = await HistoryService.GetCompletedSessionsAsync(new SessionQuery(
            Page: _currentPage,
            PageSize: 10,
            CharacterId: CharacterId,
            SortBy: SessionSortColumn.StartTime,
            SortDescending: true));
    }

    private async Task GoToPage(int page)
    {
        _currentPage = page;
        await LoadSessionsAsync();
    }

    private static string FormatDuration(TimeSpan duration) =>
        duration.Days > 0
            ? duration.ToString(@"d\.hh\:mm\:ss")
            : duration.ToString(@"hh\:mm\:ss");
}
```

(This intentionally renders its own small session table rather than extracting a shared component from `Sessions.razor` — the two tables differ enough in scope/behavior, and `Sessions.razor` is well-established and tested by manual use; not worth risking a refactor of it for this feature.)

- [ ] **Step 2: Build to verify**

Run: `dotnet build AlbionCompanion.App/AlbionCompanion.App.csproj -f net10.0-windows10.0.19041.0 -r win-x64`
Expected: succeeds with 0 errors, 0 warnings.

- [ ] **Step 3: Commit**

```bash
git add AlbionCompanion.App/Components/Pages/CharacterDashboard.razor
git commit -m "feat(app): add per-character dashboard with session history"
```

---

### Task 10: Broadcast page (character-scoped live view, replaces the old Home)

**Files:**
- Create: `AlbionCompanion.App/Components/Pages/Broadcast.razor`

**Interfaces:**
- Consumes: `IGatheringLiveState.CharacterId`/`.IsActive`/everything else already used by the old `Home.razor` (unchanged).

- [ ] **Step 1: Write `Broadcast.razor`**

Write `AlbionCompanion.App/Components/Pages/Broadcast.razor` — this is the old `Home.razor` content (deleted in Task 8) with a route change, a back link, a "Broadcast" label, and a character-mismatch guard:

```razor
@page "/characters/{CharacterId:guid}/broadcast"
@using AlbionCompanion.Gathering
@inject IGatheringLiveState LiveState
@implements IDisposable

<a class="ac-back-link" href="@($"/characters/{CharacterId}")">
    <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="m12 19-7-7 7-7" /><path d="M19 12H5" /></svg>
    Back
</a>

<p class="ac-subtitle" style="text-transform:uppercase; letter-spacing:0.08em; font-size:11px;">Broadcast</p>

@if (!LiveState.IsActive || LiveState.CharacterId != CharacterId)
{
    <div class="ac-empty">
        <span class="ac-dot ac-dot-ended"></span>
        <h1 class="ac-status-title">Not currently broadcasting</h1>
        <p class="ac-subtitle">This character has no active session right now.</p>
    </div>
}
else
{
    <div class="ac-status-row">
        <span class="ac-dot @(LiveState.IsActive ? "ac-dot-active" : "ac-dot-ended")"></span>
        <h1 class="ac-status-title">@(LiveState.IsActive ? "Active" : "Ended") — @LiveState.CurrentLocation</h1>
    </div>

    <div class="ac-card-row">
        <div class="ac-card">
            <div class="ac-card-head">
                <div class="ac-card-kicker">Total — this session</div>
            </div>
            <div class="ac-card-stats">
                <div class="ac-card-stat">
                    <div class="ac-card-stat-label">Fame</div>
                    <div class="ac-card-value">@LiveState.TotalFame.ToString("N0")</div>
                </div>
                <div class="ac-card-stat">
                    <div class="ac-card-stat-label">Items</div>
                    <div class="ac-card-value">@TotalItems.ToString("N0")</div>
                </div>
                <div class="ac-card-stat">
                    <div class="ac-card-stat-label">Silver</div>
                    <div class="ac-card-value">@LiveState.TotalSilver.ToString("N0")</div>
                </div>
            </div>
        </div>

        @foreach (var location in LocationBreakdown)
        {
            <div class="ac-card">
                <div class="ac-card-head">
                    <div class="ac-card-kicker">@location.Location</div>
                </div>
                <div class="ac-card-stats">
                    <div class="ac-card-stat">
                        <div class="ac-card-stat-label">Fame</div>
                        <div class="ac-card-value">@location.Fame.ToString("N0")</div>
                    </div>
                    <div class="ac-card-stat">
                        <div class="ac-card-stat-label">Items</div>
                        <div class="ac-card-value">@location.Items.ToString("N0")</div>
                    </div>
                    <div class="ac-card-stat">
                        <div class="ac-card-stat-label">Silver</div>
                        <div class="ac-card-value">@location.Silver.ToString("N0")</div>
                    </div>
                </div>
            </div>
        }
    </div>

    @if (LiveState.ItemTotals.Count == 0)
    {
        <p class="ac-subtitle">No items collected yet.</p>
    }
    else
    {
        <ItemTable Items="LiveState.ItemTotals" Title="Items gathered — live" />
    }
}

@code {
    [Parameter]
    public Guid CharacterId { get; set; }

    private int TotalItems => LiveState.ItemTotals.Sum(t => t.Amount);

    private List<(string Location, int Fame, int Items, int Silver)> LocationBreakdown
    {
        get
        {
            var locations = LiveState.ItemTotals.Select(t => t.Location)
                .Concat(LiveState.FameByLocation.Select(f => f.Location))
                .Concat(LiveState.SilverByLocation.Select(s => s.Location))
                .Distinct()
                .ToList();

            // Only worth showing once a session has actually roamed through more than one
            // zone - a single-location session would just duplicate the aggregate card above.
            if (locations.Count <= 1)
            {
                return new List<(string, int, int, int)>();
            }

            var itemsByLocation = LiveState.ItemTotals
                .GroupBy(t => t.Location)
                .ToDictionary(g => g.Key, g => g.Sum(t => t.Amount));
            var fameByLocation = LiveState.FameByLocation
                .ToDictionary(f => f.Location, f => f.Amount);
            var silverByLocation = LiveState.SilverByLocation
                .ToDictionary(s => s.Location, s => s.Amount);

            return locations
                .Select(location => (
                    Location: location,
                    Fame: fameByLocation.GetValueOrDefault(location),
                    Items: itemsByLocation.GetValueOrDefault(location),
                    Silver: silverByLocation.GetValueOrDefault(location)))
                .OrderByDescending(l => l.Fame + l.Items + l.Silver)
                .ToList();
        }
    }

    protected override void OnInitialized()
    {
        LiveState.OnChanged += HandleChanged;
    }

    private void HandleChanged(object? sender, EventArgs e) =>
        InvokeAsync(StateHasChanged);

    public void Dispose()
    {
        LiveState.OnChanged -= HandleChanged;
    }
}
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build AlbionCompanion.App/AlbionCompanion.App.csproj -f net10.0-windows10.0.19041.0 -r win-x64`
Expected: succeeds with 0 errors, 0 warnings.

- [ ] **Step 3: Commit**

```bash
git add AlbionCompanion.App/Components/Pages/Broadcast.razor
git commit -m "feat(app): add character-scoped Broadcast live view"
```

---

### Task 11: Session-start toast notification

**Files:**
- Create: `AlbionCompanion.App/Components/SessionStartToast.razor`
- Modify: `AlbionCompanion.App/Components/Layout/MainLayout.razor`

**Interfaces:**
- Consumes: `IGatheringLiveState.OnSessionStarted` (Task 6), `ICharacterService.GetOverviewAsync(Guid)` (Task 2).

- [ ] **Step 1: Write `SessionStartToast.razor`**

Write `AlbionCompanion.App/Components/SessionStartToast.razor`:

```razor
@using AlbionCompanion.Core.Models
@using AlbionCompanion.Gathering
@inject IGatheringLiveState LiveState
@inject ICharacterService CharacterService
@implements IDisposable

@if (_visible && _characterId is not null && _characterName is not null)
{
    <div class="ac-toast">
        <span>Session started for <strong>@_characterName</strong></span>
        <a class="ac-toast-link" href="@($"/characters/{_characterId}/broadcast")">View</a>
        <button class="ac-toast-close" @onclick="Dismiss">✕</button>
    </div>
}

@code {
    private bool _visible;
    private Guid? _characterId;
    private string? _characterName;
    private CancellationTokenSource? _autoHide;

    protected override void OnInitialized()
    {
        LiveState.OnSessionStarted += HandleSessionStarted;
    }

    // Skips the toast entirely when the session has no resolved character (an unregistered
    // character's name, or no zone-join/PlayerAnnounce match yet) - there's nowhere useful to
    // link the user to yet.
    private void HandleSessionStarted(object? sender, GatheringSession session)
    {
        if (session.CharacterId is not { } characterId)
        {
            return;
        }

        _ = ShowAsync(characterId);
    }

    private async Task ShowAsync(Guid characterId)
    {
        var overview = await CharacterService.GetOverviewAsync(characterId);
        if (overview is null)
        {
            return;
        }

        _characterId = characterId;
        _characterName = overview.Name;
        _visible = true;
        await InvokeAsync(StateHasChanged);

        _autoHide?.Cancel();
        var cts = new CancellationTokenSource();
        _autoHide = cts;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(8), cts.Token);
            _visible = false;
            await InvokeAsync(StateHasChanged);
        }
        catch (TaskCanceledException)
        {
            // Dismiss() cancelled the auto-hide timer - nothing more to do.
        }
    }

    private void Dismiss()
    {
        _autoHide?.Cancel();
        _visible = false;
    }

    public void Dispose()
    {
        LiveState.OnSessionStarted -= HandleSessionStarted;
        _autoHide?.Cancel();
    }
}
```

- [ ] **Step 2: Add it to `MainLayout.razor`**

Replace the full contents of `AlbionCompanion.App/Components/Layout/MainLayout.razor`:

```razor
@inherits LayoutComponentBase

<div class="ac-shell">
    <div class="ac-sidebar">
        <NavMenu />
    </div>

    <main class="ac-main">
        @Body
    </main>
</div>

<SessionStartToast />
```

- [ ] **Step 3: Build to verify**

Run: `dotnet build AlbionCompanion.App/AlbionCompanion.App.csproj -f net10.0-windows10.0.19041.0 -r win-x64`
Expected: succeeds with 0 errors, 0 warnings.

- [ ] **Step 4: Commit**

```bash
git add AlbionCompanion.App/Components/SessionStartToast.razor AlbionCompanion.App/Components/Layout/MainLayout.razor
git commit -m "feat(app): add self-dismissing toast when a new session starts"
```

---

### Task 12: `Sessions.razor` gets a Character column

**Files:**
- Modify: `AlbionCompanion.App/Components/Pages/Sessions.razor`

**Interfaces:**
- Consumes: `SessionSummary.CharacterName` (Task 7).

- [ ] **Step 1: Add the column**

In `AlbionCompanion.App/Components/Pages/Sessions.razor`, add a header cell right after the Zone column's `<th>`. Change:

```razor
            <tr>
                <th><button class="ac-sort-btn" @onclick="() => SetSort(SessionSortColumn.Location)">Zone @SortIndicator(SessionSortColumn.Location)</button></th>
                <th><button class="ac-sort-btn" @onclick="() => SetSort(SessionSortColumn.StartTime)">Start @SortIndicator(SessionSortColumn.StartTime)</button></th>
```

to:

```razor
            <tr>
                <th><button class="ac-sort-btn" @onclick="() => SetSort(SessionSortColumn.Location)">Zone @SortIndicator(SessionSortColumn.Location)</button></th>
                <th>Character</th>
                <th><button class="ac-sort-btn" @onclick="() => SetSort(SessionSortColumn.StartTime)">Start @SortIndicator(SessionSortColumn.StartTime)</button></th>
```

Add the matching data cell right after the Zone `<td>`. Change:

```razor
                    <td class="ac-item-cell">
                        <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" style="opacity:.55;"><path d="M20 10c0 4.993-5.539 10.193-7.399 11.799a1 1 0 0 1-1.202 0C9.539 20.193 4 14.993 4 10a8 8 0 0 1 16 0" /><circle cx="12" cy="10" r="3" /></svg>
                        @string.Join(", ", session.Locations)
                    </td>
                    <td>@session.StartTime.ToLocalTime().ToString("g")</td>
```

to:

```razor
                    <td class="ac-item-cell">
                        <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" style="opacity:.55;"><path d="M20 10c0 4.993-5.539 10.193-7.399 11.799a1 1 0 0 1-1.202 0C9.539 20.193 4 14.993 4 10a8 8 0 0 1 16 0" /><circle cx="12" cy="10" r="3" /></svg>
                        @string.Join(", ", session.Locations)
                    </td>
                    <td class="ac-cell-muted">@session.CharacterName</td>
                    <td>@session.StartTime.ToLocalTime().ToString("g")</td>
```

(`.ac-cell-muted` already exists — added earlier this session for the gathered-items table's Location column.)

- [ ] **Step 2: Build to verify**

Run: `dotnet build AlbionCompanion.App/AlbionCompanion.App.csproj -f net10.0-windows10.0.19041.0 -r win-x64`
Expected: succeeds with 0 errors, 0 warnings.

- [ ] **Step 3: Commit**

```bash
git add AlbionCompanion.App/Components/Pages/Sessions.razor
git commit -m "feat(app): show which character each session belongs to in the sessions list"
```

---

## Final verification (after all tasks)

- [ ] Run the full test suite: `dotnet test AlbionCompanion.Core.Tests/AlbionCompanion.Core.Tests.csproj && dotnet test AlbionCompanion.Gathering.Tests/AlbionCompanion.Gathering.Tests.csproj`
  Expected: all green.
- [ ] Run a full build: `dotnet build AlbionCompanion.App/AlbionCompanion.App.csproj -f net10.0-windows10.0.19041.0 -r win-x64`
  Expected: 0 errors, 0 warnings.
- [ ] Ask the user to delete `%APPDATA%\AlbionCompanion\albion.db` (a fresh DB picks up the new migrations cleanly) or confirm they're fine letting the existing DB migrate in place, then launch the app and manually verify:
  - `/` shows the character hub (empty state if no characters registered yet).
  - Adding a character with a duplicate name shows the "already registered" error instead of crashing.
  - Playing the game with a registered character's exact name shows a toast on session start, linking to that character's Broadcast page.
  - `/characters/{id}` shows correct aggregate stats and a working session list/pager.
  - `/characters/{id}/broadcast` shows live data while active, and the "not currently broadcasting" message when navigated to for an inactive character.
  - `/sessions` shows the new Character column.
