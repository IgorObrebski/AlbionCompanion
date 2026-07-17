# Session History View Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a "Sessions" page listing completed gathering sessions (date, location, duration, fame, item count) and a details page per session showing its aggregated item breakdown, backed by a new query-only `ISessionHistoryService`.

**Architecture:** Task 1 adds `ISessionHistoryService`/`SessionHistoryService` to `AlbionCompanion.Gathering`, querying `AppDbContext` via `IDbContextFactory<AppDbContext>` (matching `RawEventRecorder`'s existing pattern) — no changes to how sessions are written. Task 2 wires it into `AlbionCompanion.App`: registered in MAUI's DI container, and rendered by two new Blazor pages (`Sessions.razor` list, `SessionDetail.razor` detail), with a second `NavMenu.razor` entry.

**Tech Stack:** C# / .NET 10, EF Core (SQLite), MAUI Blazor Hybrid, xUnit.

## Global Constraints

- The session list shows only completed sessions (`EndTime != null`) — the active session is already shown on the Home/live page, not duplicated here.
- The details page shows an aggregated item-totals table (item → summed amount), not a chronological per-event log.
- `GatheredItem.ItemId` is shown as-is — no `IItemDictionaryService` resolution.
- No changes to `GatheringSessionService`, `GatheringEventRouter`, or how sessions/items/fame are written.

---

### Task 1: `ISessionHistoryService` / `SessionHistoryService`

**Files:**
- Create: `AlbionCompanion.Gathering/ISessionHistoryService.cs`
- Create: `AlbionCompanion.Gathering/SessionHistoryService.cs`
- Test: `AlbionCompanion.Gathering.Tests/SessionHistoryServiceTests.cs`

**Interfaces:**
- Produces: `AlbionCompanion.Gathering.ISessionHistoryService` with `Task<IReadOnlyList<SessionSummary>> GetCompletedSessionsAsync()` and `Task<SessionDetail?> GetSessionDetailAsync(Guid sessionId)`; records `SessionSummary(Guid Id, DateTime StartTime, DateTime EndTime, string StartLocation, int TotalFameEarned, int TotalItemsCollected)` and `SessionDetail(Guid Id, DateTime StartTime, DateTime EndTime, string StartLocation, int TotalFameEarned, IReadOnlyDictionary<string, int> ItemTotals)`. Consumed by Task 2 (`Sessions.razor`/`SessionDetail.razor`/`MauiProgram.cs`).

The full new `AlbionCompanion.Gathering/ISessionHistoryService.cs`:

```csharp
namespace AlbionCompanion.Gathering;

public interface ISessionHistoryService
{
    Task<IReadOnlyList<SessionSummary>> GetCompletedSessionsAsync();
    Task<SessionDetail?> GetSessionDetailAsync(Guid sessionId);
}

public record SessionSummary(
    Guid Id,
    DateTime StartTime,
    DateTime EndTime,
    string StartLocation,
    int TotalFameEarned,
    int TotalItemsCollected);

public record SessionDetail(
    Guid Id,
    DateTime StartTime,
    DateTime EndTime,
    string StartLocation,
    int TotalFameEarned,
    IReadOnlyDictionary<string, int> ItemTotals);
```

The full new `AlbionCompanion.Gathering/SessionHistoryService.cs`:

```csharp
using AlbionCompanion.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace AlbionCompanion.Gathering;

public class SessionHistoryService : ISessionHistoryService
{
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;

    public SessionHistoryService(IDbContextFactory<AppDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory;
    }

    public async Task<IReadOnlyList<SessionSummary>> GetCompletedSessionsAsync()
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();

        return await dbContext.GatheringSessions
            .Where(s => s.EndTime != null)
            .OrderByDescending(s => s.StartTime)
            .Select(s => new SessionSummary(
                s.Id,
                s.StartTime,
                s.EndTime!.Value,
                s.StartLocation,
                s.TotalFameEarned,
                s.GatheredItems.Sum(i => (int?)i.Amount) ?? 0))
            .ToListAsync();
    }

    public async Task<SessionDetail?> GetSessionDetailAsync(Guid sessionId)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();

        var session = await dbContext.GatheringSessions
            .Include(s => s.GatheredItems)
            .SingleOrDefaultAsync(s => s.Id == sessionId && s.EndTime != null);

        if (session is null)
        {
            return null;
        }

        var itemTotals = session.GatheredItems
            .GroupBy(i => i.ItemId)
            .ToDictionary(g => g.Key, g => g.Sum(i => i.Amount));

        return new SessionDetail(
            session.Id,
            session.StartTime,
            session.EndTime!.Value,
            session.StartLocation,
            session.TotalFameEarned,
            itemTotals);
    }
}
```

- [ ] **Step 1: Write the failing tests**

Create `AlbionCompanion.Gathering.Tests/SessionHistoryServiceTests.cs`:

```csharp
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
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test AlbionCompanion.Gathering.Tests --filter SessionHistoryServiceTests`
Expected: FAIL — build error, `ISessionHistoryService`/`SessionHistoryService` do not exist.

- [ ] **Step 3: Create `ISessionHistoryService.cs` and `SessionHistoryService.cs` with the exact content above**

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test AlbionCompanion.Gathering.Tests --filter SessionHistoryServiceTests`
Expected: PASS — all 7 facts pass.

- [ ] **Step 5: Run the full test suite**

Run: `dotnet build && dotnet test`
Expected: Build succeeds, all tests across the solution PASS (this task only adds two new files plus one new test file — no other project is affected).

- [ ] **Step 6: Commit**

```bash
git add AlbionCompanion.Gathering/ISessionHistoryService.cs AlbionCompanion.Gathering/SessionHistoryService.cs AlbionCompanion.Gathering.Tests/SessionHistoryServiceTests.cs
git commit -m "feat(gathering): add SessionHistoryService for querying completed sessions"
```

---

### Task 2: Wire `ISessionHistoryService` into `AlbionCompanion.App` with list + detail pages

**Files:**
- Modify: `AlbionCompanion.App/MauiProgram.cs`
- Create: `AlbionCompanion.App/Components/Pages/Sessions.razor`
- Create: `AlbionCompanion.App/Components/Pages/SessionDetail.razor`
- Modify: `AlbionCompanion.App/Components/Layout/NavMenu.razor`

**Interfaces:**
- Consumes: `AlbionCompanion.Gathering.ISessionHistoryService`, `SessionSummary`, `SessionDetail` (Task 1); `AlbionCompanion.Gathering.AppHostBuilder` (existing); `Microsoft.EntityFrameworkCore.IDbContextFactory<AppDbContext>` (existing, registered in `AppHostBuilder.BuildServiceProvider`).
- Produces: nothing consumed by a later task — this is the final task.

The full new `AlbionCompanion.App/MauiProgram.cs`:

```csharp
using AlbionCompanion.Core.Data;
using AlbionCompanion.Gathering;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AlbionCompanion.App;

public static class MauiProgram
{
    public static ServiceProvider? GatheringProvider { get; private set; }
    public static IServiceScope? GatheringSessionScope { get; set; }
    public static IServiceProvider? Services { get; private set; }

    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
            });

        builder.Services.AddMauiBlazorWebView();
        builder.Services.AddSingleton<IGatheringLiveState, GatheringLiveState>();
        builder.Services.AddSingleton<ISessionHistoryService>(_ =>
            new SessionHistoryService(GatheringProvider!.GetRequiredService<IDbContextFactory<AppDbContext>>()));

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif

        var appDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AlbionCompanion");
        Directory.CreateDirectory(appDataPath);
        GatheringProvider = AppHostBuilder.BuildServiceProvider(appDataPath);

        var app = builder.Build();
        Services = app.Services;
        return app;
    }
}
```

(The `AddSingleton<ISessionHistoryService>` factory delegate is not invoked until first resolved,
which only happens once a Blazor page injects it — by then `GatheringProvider` is already assigned
on line "GatheringProvider = AppHostBuilder.BuildServiceProvider(appDataPath);", which runs before
`builder.Build()` returns control to any page. No ordering issue despite the registration appearing
textually before that assignment.)

The full new `AlbionCompanion.App/Components/Pages/Sessions.razor`:

```razor
@page "/sessions"
@using AlbionCompanion.Gathering
@inject ISessionHistoryService HistoryService

<h1>Sessions</h1>

@if (_sessions is null)
{
    <p>Loading...</p>
}
else if (_sessions.Count == 0)
{
    <p>No completed sessions yet.</p>
}
else
{
    <table class="table">
        <thead>
            <tr>
                <th>Start</th>
                <th>Location</th>
                <th>Duration</th>
                <th>Fame</th>
                <th>Items</th>
            </tr>
        </thead>
        <tbody>
            @foreach (var session in _sessions)
            {
                <tr @onclick="() => NavigateToDetail(session.Id)" style="cursor:pointer">
                    <td>@session.StartTime.ToLocalTime().ToString("g")</td>
                    <td>@session.StartLocation</td>
                    <td>@((session.EndTime - session.StartTime).ToString(@"hh\:mm\:ss"))</td>
                    <td>@session.TotalFameEarned</td>
                    <td>@session.TotalItemsCollected</td>
                </tr>
            }
        </tbody>
    </table>
}

@code {
    private IReadOnlyList<SessionSummary>? _sessions;

    [Inject]
    private NavigationManager Navigation { get; set; } = null!;

    protected override async Task OnInitializedAsync()
    {
        _sessions = await HistoryService.GetCompletedSessionsAsync();
    }

    private void NavigateToDetail(Guid sessionId) =>
        Navigation.NavigateTo($"/sessions/{sessionId}");
}
```

The full new `AlbionCompanion.App/Components/Pages/SessionDetail.razor`:

```razor
@page "/sessions/{SessionId:guid}"
@using AlbionCompanion.Gathering
@inject ISessionHistoryService HistoryService

<h1>Session Detail</h1>

@if (_detail is null)
{
    <p>Session not found.</p>
}
else
{
    <p>@_detail.StartLocation — @_detail.StartTime.ToLocalTime().ToString("g") to @_detail.EndTime.ToLocalTime().ToString("g")</p>
    <p>Total fame: @_detail.TotalFameEarned</p>

    @if (_detail.ItemTotals.Count == 0)
    {
        <p>No items collected.</p>
    }
    else
    {
        <table class="table">
            <thead>
                <tr><th>Item</th><th>Amount</th></tr>
            </thead>
            <tbody>
                @foreach (var (itemId, amount) in _detail.ItemTotals)
                {
                    <tr><td>@itemId</td><td>@amount</td></tr>
                }
            </tbody>
        </table>
    }
}

<p><a href="/sessions">Back to sessions</a></p>

@code {
    [Parameter]
    public Guid SessionId { get; set; }

    private SessionDetail? _detail;

    protected override async Task OnParametersSetAsync()
    {
        _detail = await HistoryService.GetSessionDetailAsync(SessionId);
    }
}
```

The full new `AlbionCompanion.App/Components/Layout/NavMenu.razor`:

```razor
<div class="top-row ps-3 navbar navbar-dark">
    <div class="container-fluid">
        <a class="navbar-brand" href="">AlbionCompanion.App</a>
    </div>
</div>

<input type="checkbox" title="Navigation menu" class="navbar-toggler" />

<div class="nav-scrollable" onclick="document.querySelector('.navbar-toggler').click()">
    <nav class="nav flex-column">
        <div class="nav-item px-3">
            <NavLink class="nav-link" href="" Match="NavLinkMatch.All">
                <span class="bi bi-house-door-fill-nav-menu" aria-hidden="true"></span> Home
            </NavLink>
        </div>
        <div class="nav-item px-3">
            <NavLink class="nav-link" href="sessions">
                <span class="bi bi-list-nested-nav-menu" aria-hidden="true"></span> Sessions
            </NavLink>
        </div>
    </nav>
</div>
```

- [ ] **Step 1: Replace `MauiProgram.cs`, create the two new `.razor` pages, and replace `NavMenu.razor` with the exact content above**

- [ ] **Step 2: Build the whole solution**

Run: `dotnet build`
Expected: Build succeeds for every project including `AlbionCompanion.App`.

- [ ] **Step 3: Run the full test suite**

Run: `dotnet test`
Expected: Same pass count as Task 1's Step 5 — this task adds no new unit-testable logic (pure UI wiring), so no new tests are added here.

- [ ] **Step 4: Manual smoke test**

Run: `dotnet run --project AlbionCompanion.App`
Expected: A window opens; the nav menu shows "Home" and "Sessions". Clicking "Sessions" shows "No
completed sessions yet." on a fresh database (or a populated table if past sessions already exist
in `%APPDATA%\AlbionCompanion\albion.db`). Clicking a row (if any sessions exist) navigates to its
detail page showing the item breakdown; "Back to sessions" returns to the list. Close the window;
confirm the process exits cleanly.

- [ ] **Step 5: Commit**

```bash
git add AlbionCompanion.App/MauiProgram.cs AlbionCompanion.App/Components/Pages/Sessions.razor AlbionCompanion.App/Components/Pages/SessionDetail.razor AlbionCompanion.App/Components/Layout/NavMenu.razor
git commit -m "feat(app): add session history list and detail pages"
```

---

## Self-Review Notes

- Spec coverage: `ISessionHistoryService` with both methods and exact DTO shapes from the spec (Task 1); completed-only filter, most-recent-first ordering, aggregated item totals, "no detail for active session" rule, all covered by the 7 listed test cases (Task 1); MAUI DI registration via the same cross-container factory pattern as `IGatheringLiveState`, list page, detail page, nav entry (Task 2). Non-goals respected: no chronological event log, no `IItemDictionaryService` resolution, no editing/deleting, no pagination.
- No placeholders — every step has literal file contents, exact commands, and expected output.
- Type/name consistency: `SessionSummary`/`SessionDetail` field names match between Task 1's definition, its tests, and Task 2's `Sessions.razor`/`SessionDetail.razor` usage (`StartTime`, `EndTime`, `StartLocation`, `TotalFameEarned`, `TotalItemsCollected`, `ItemTotals`).
