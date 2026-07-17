# Live Session View Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Show the active gathering session live in `AlbionCompanion.App` — items collected, total fame, location, status — driven by `GatheringSessionService`'s existing domain events, with the last session's tally staying visible until the next one starts.

**Architecture:** Task 1 adds a plain-C# `IGatheringLiveState`/`GatheringLiveState` adapter to `AlbionCompanion.Gathering` (not `AlbionCompanion.App` — it has no MAUI dependency, and putting it in `Gathering` lets it be unit-tested in the existing `AlbionCompanion.Gathering.Tests` project instead of requiring a new test project that can reference a MAUI SDK project, which is unnecessarily awkward). Task 2 wires it into `AlbionCompanion.App`: registered in MAUI's DI container, attached to the real `IGatheringSessionService` once startup finishes, and rendered by a rewritten `Home.razor`.

**Tech Stack:** C# / .NET 10, MAUI Blazor Hybrid, xUnit.

## Global Constraints

- Event handlers never throw past their own boundary and never hold a reference to the live EF-tracked entities from event payloads — only primitive fields are copied out.
- `OnSessionEnded` does not clear `ItemTotals`/`TotalFame`/`StartLocation` — they stay as the last session's final tally until the next `OnSessionStarted`.
- `GatheredItem.ItemId` (raw `UniqueName`) is shown as-is — no resolution through `IItemDictionaryService` in this plan.
- `Attach` is called exactly once, explicitly, after `AppHostBuilder.RunStartupSequenceAsync` completes — not via polling.

---

### Task 1: `IGatheringLiveState` / `GatheringLiveState` adapter

**Files:**
- Create: `AlbionCompanion.Gathering/IGatheringLiveState.cs`
- Create: `AlbionCompanion.Gathering/GatheringLiveState.cs`
- Test: `AlbionCompanion.Gathering.Tests/GatheringLiveStateTests.cs`

**Interfaces:**
- Produces: `AlbionCompanion.Gathering.IGatheringLiveState` with members `bool IsActive { get; }`, `string? StartLocation { get; }`, `int TotalFame { get; }`, `IReadOnlyDictionary<string, int> ItemTotals { get; }`, `event EventHandler? OnChanged`, `void Attach(IGatheringSessionService sessionService)`. Consumed by Task 2 (DI registration + `Home.razor`).

The full new `AlbionCompanion.Gathering/IGatheringLiveState.cs`:

```csharp
namespace AlbionCompanion.Gathering;

public interface IGatheringLiveState
{
    bool IsActive { get; }
    string? StartLocation { get; }
    int TotalFame { get; }
    IReadOnlyDictionary<string, int> ItemTotals { get; }

    event EventHandler? OnChanged;

    void Attach(IGatheringSessionService sessionService);
}
```

The full new `AlbionCompanion.Gathering/GatheringLiveState.cs`:

```csharp
namespace AlbionCompanion.Gathering;

public class GatheringLiveState : IGatheringLiveState
{
    private readonly Dictionary<string, int> _itemTotals = new();

    public bool IsActive { get; private set; }
    public string? StartLocation { get; private set; }
    public int TotalFame { get; private set; }
    public IReadOnlyDictionary<string, int> ItemTotals => _itemTotals;

    public event EventHandler? OnChanged;

    public void Attach(IGatheringSessionService sessionService)
    {
        sessionService.OnSessionStarted += (_, session) => Safely(() =>
        {
            _itemTotals.Clear();
            TotalFame = 0;
            StartLocation = session.StartLocation;
            IsActive = true;
        });

        sessionService.OnSessionEnded += (_, _) => Safely(() =>
        {
            IsActive = false;
        });

        sessionService.OnItemAdded += (_, item) => Safely(() =>
        {
            var itemId = item.ItemId;
            var amount = item.Amount;
            _itemTotals[itemId] = _itemTotals.GetValueOrDefault(itemId) + amount;
        });

        sessionService.OnFameAdded += (_, fameLog) => Safely(() =>
        {
            TotalFame += fameLog.Amount;
        });
    }

    private void Safely(Action update)
    {
        try
        {
            update();
            OnChanged?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // Handlers on IGatheringSessionService's events must never throw past this boundary -
            // they run inline from GatheringEventRouter's fire-and-forget dispatch, and an
            // unhandled exception here would be lost as an unobserved task exception anyway. A
            // failed UI-state update is preferable to destabilizing the gathering pipeline.
        }
    }
}
```

- [ ] **Step 1: Write the failing tests**

Create `AlbionCompanion.Gathering.Tests/GatheringLiveStateTests.cs`:

```csharp
namespace AlbionCompanion.Gathering.Tests;

public class GatheringLiveStateTests
{
    private sealed class FakeGatheringSessionService : IGatheringSessionService
    {
        public event EventHandler<GatheringSession>? OnSessionStarted;
        public event EventHandler<GatheringSession>? OnSessionEnded;
        public event EventHandler<AlbionCompanion.Core.Models.GatheredItem>? OnItemAdded;
        public event EventHandler<AlbionCompanion.Core.Models.FameLog>? OnFameAdded;

        public Task StartSessionAsync(string location) => Task.CompletedTask;
        public Task EndSessionAsync() => Task.CompletedTask;
        public Task AddItemAsync(string itemId, int amount) => Task.CompletedTask;
        public Task AddFameAsync(string fameType, int amount) => Task.CompletedTask;
        public Task<GatheringSession?> GetActiveSessionAsync() => Task.FromResult<GatheringSession?>(null);

        public void RaiseSessionStarted(GatheringSession session) => OnSessionStarted?.Invoke(this, session);
        public void RaiseSessionEnded(GatheringSession session) => OnSessionEnded?.Invoke(this, session);
        public void RaiseItemAdded(AlbionCompanion.Core.Models.GatheredItem item) => OnItemAdded?.Invoke(this, item);
        public void RaiseFameAdded(AlbionCompanion.Core.Models.FameLog fameLog) => OnFameAdded?.Invoke(this, fameLog);
    }

    [Fact]
    public void OnItemAdded_NewItem_AppearsInItemTotals()
    {
        var liveState = new GatheringLiveState();
        var service = new FakeGatheringSessionService();
        liveState.Attach(service);

        service.RaiseItemAdded(new AlbionCompanion.Core.Models.GatheredItem { ItemId = "T4_ORE", Amount = 5 });

        Assert.Equal(5, liveState.ItemTotals["T4_ORE"]);
    }

    [Fact]
    public void OnItemAdded_SameItemTwice_AmountsSum()
    {
        var liveState = new GatheringLiveState();
        var service = new FakeGatheringSessionService();
        liveState.Attach(service);

        service.RaiseItemAdded(new AlbionCompanion.Core.Models.GatheredItem { ItemId = "T4_ORE", Amount = 5 });
        service.RaiseItemAdded(new AlbionCompanion.Core.Models.GatheredItem { ItemId = "T4_ORE", Amount = 3 });

        Assert.Equal(8, liveState.ItemTotals["T4_ORE"]);
    }

    [Fact]
    public void OnFameAdded_Twice_TotalFameAccumulates()
    {
        var liveState = new GatheringLiveState();
        var service = new FakeGatheringSessionService();
        liveState.Attach(service);

        service.RaiseFameAdded(new AlbionCompanion.Core.Models.FameLog { FameType = "Gathering", Amount = 300 });
        service.RaiseFameAdded(new AlbionCompanion.Core.Models.FameLog { FameType = "Gathering", Amount = 600 });

        Assert.Equal(900, liveState.TotalFame);
    }

    [Fact]
    public void OnSessionStarted_AfterPriorActivity_ResetsState()
    {
        var liveState = new GatheringLiveState();
        var service = new FakeGatheringSessionService();
        liveState.Attach(service);
        service.RaiseItemAdded(new AlbionCompanion.Core.Models.GatheredItem { ItemId = "T4_ORE", Amount = 5 });
        service.RaiseFameAdded(new AlbionCompanion.Core.Models.FameLog { FameType = "Gathering", Amount = 300 });

        service.RaiseSessionStarted(new GatheringSession { StartLocation = "Martlock" });

        Assert.Empty(liveState.ItemTotals);
        Assert.Equal(0, liveState.TotalFame);
        Assert.True(liveState.IsActive);
        Assert.Equal("Martlock", liveState.StartLocation);
    }

    [Fact]
    public void OnSessionEnded_LeavesDataUnchangedButMarksInactive()
    {
        var liveState = new GatheringLiveState();
        var service = new FakeGatheringSessionService();
        liveState.Attach(service);
        service.RaiseSessionStarted(new GatheringSession { StartLocation = "Martlock" });
        service.RaiseItemAdded(new AlbionCompanion.Core.Models.GatheredItem { ItemId = "T4_ORE", Amount = 5 });
        service.RaiseFameAdded(new AlbionCompanion.Core.Models.FameLog { FameType = "Gathering", Amount = 300 });

        service.RaiseSessionEnded(new GatheringSession { StartLocation = "Martlock" });

        Assert.False(liveState.IsActive);
        Assert.Equal(5, liveState.ItemTotals["T4_ORE"]);
        Assert.Equal(300, liveState.TotalFame);
        Assert.Equal("Martlock", liveState.StartLocation);
    }

    [Fact]
    public void EachHandler_RaisesOnChangedExactlyOnce()
    {
        var liveState = new GatheringLiveState();
        var service = new FakeGatheringSessionService();
        liveState.Attach(service);
        var raiseCount = 0;
        liveState.OnChanged += (_, _) => raiseCount++;

        service.RaiseSessionStarted(new GatheringSession { StartLocation = "Martlock" });
        service.RaiseItemAdded(new AlbionCompanion.Core.Models.GatheredItem { ItemId = "T4_ORE", Amount = 5 });
        service.RaiseFameAdded(new AlbionCompanion.Core.Models.FameLog { FameType = "Gathering", Amount = 300 });
        service.RaiseSessionEnded(new GatheringSession { StartLocation = "Martlock" });

        Assert.Equal(4, raiseCount);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test AlbionCompanion.Gathering.Tests --filter GatheringLiveStateTests`
Expected: FAIL — build error, `IGatheringLiveState`/`GatheringLiveState` do not exist.

- [ ] **Step 3: Create `IGatheringLiveState.cs` and `GatheringLiveState.cs` with the exact content above**

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test AlbionCompanion.Gathering.Tests --filter GatheringLiveStateTests`
Expected: PASS — all 6 facts pass.

- [ ] **Step 5: Run the full test suite**

Run: `dotnet build && dotnet test`
Expected: Build succeeds, all tests across the solution PASS (no other project is affected — this task only adds two new files).

- [ ] **Step 6: Commit**

```bash
git add AlbionCompanion.Gathering/IGatheringLiveState.cs AlbionCompanion.Gathering/GatheringLiveState.cs AlbionCompanion.Gathering.Tests/GatheringLiveStateTests.cs
git commit -m "feat(gathering): add GatheringLiveState adapter for live session UI"
```

---

### Task 2: Wire `GatheringLiveState` into `AlbionCompanion.App` and render it

**Files:**
- Modify: `AlbionCompanion.App/MauiProgram.cs`
- Modify: `AlbionCompanion.App/App.xaml.cs`
- Modify: `AlbionCompanion.App/Components/Pages/Home.razor`

**Interfaces:**
- Consumes: `AlbionCompanion.Gathering.IGatheringLiveState`/`GatheringLiveState` (Task 1); `AlbionCompanion.Gathering.AppHostBuilder.RunStartupSequenceAsync` (existing, from the MAUI-scaffold sub-project); `MauiApp.Services` (`IServiceProvider`, standard MAUI API — the built app's own DI container).
- Produces: nothing consumed by a later task — this is the final task.

`MauiApp` (returned by `builder.Build()`) exposes its DI container via a public `Services` property.
`MauiProgram` is extended to capture it in a new static property so `App.xaml.cs` — which is
constructed as part of that same `MauiApp`'s lifecycle — can resolve `IGatheringLiveState` from it.

The full new `AlbionCompanion.App/MauiProgram.cs`:

```csharp
using AlbionCompanion.Gathering;
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

The full new `AlbionCompanion.App/App.xaml.cs`:

```csharp
using AlbionCompanion.Gathering;
using Microsoft.Extensions.DependencyInjection;

namespace AlbionCompanion.App;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(new MainPage()) { Title = "AlbionCompanion" };

        window.Destroying += (_, _) =>
        {
            MauiProgram.GatheringProvider?.GetRequiredService<AlbionCompanion.Sniffer.PacketCapture.IPacketSniffer>().Stop();
            MauiProgram.GatheringSessionScope?.Dispose();
        };

        _ = StartGatheringAsync();

        return window;
    }

    private static async Task StartGatheringAsync()
    {
        if (MauiProgram.GatheringProvider is null)
        {
            return;
        }

        var sessionScope = await AppHostBuilder.RunStartupSequenceAsync(MauiProgram.GatheringProvider);
        MauiProgram.GatheringSessionScope = sessionScope;

        var sessionService = sessionScope.ServiceProvider.GetRequiredService<IGatheringSessionService>();
        MauiProgram.Services?.GetRequiredService<IGatheringLiveState>().Attach(sessionService);
    }
}
```

The full new `AlbionCompanion.App/Components/Pages/Home.razor`:

```razor
@page "/"
@using AlbionCompanion.Gathering
@inject IGatheringLiveState LiveState
@implements IDisposable

<h1>AlbionCompanion</h1>

@if (LiveState.StartLocation is null)
{
    <p>No session.</p>
}
else
{
    <p>@(LiveState.IsActive ? "Active" : "Ended") — @LiveState.StartLocation</p>
    <p>Total fame: @LiveState.TotalFame</p>

    @if (LiveState.ItemTotals.Count == 0)
    {
        <p>No items collected yet.</p>
    }
    else
    {
        <table class="table">
            <thead>
                <tr><th>Item</th><th>Amount</th></tr>
            </thead>
            <tbody>
                @foreach (var (itemId, amount) in LiveState.ItemTotals)
                {
                    <tr><td>@itemId</td><td>@amount</td></tr>
                }
            </tbody>
        </table>
    }
}

@code {
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

- [ ] **Step 1: Replace `MauiProgram.cs`, `App.xaml.cs`, and `Home.razor` with the exact content above**

- [ ] **Step 2: Build the whole solution**

Run: `dotnet build`
Expected: Build succeeds for every project including `AlbionCompanion.App`.

- [ ] **Step 3: Run the full test suite**

Run: `dotnet test`
Expected: Same pass count as Task 1's Step 5 plus no regressions — this task adds no new unit-testable logic (pure UI wiring), so no new tests are added here.

- [ ] **Step 4: Manual smoke test**

Run: `dotnet run --project AlbionCompanion.App`
Expected: A window titled "AlbionCompanion" opens showing "No session." (no gathering activity has
happened yet in this run). The Npcap check and DB migration run in the background as before. Close
the window; confirm the process exits cleanly.

- [ ] **Step 5: Commit**

```bash
git add AlbionCompanion.App/MauiProgram.cs AlbionCompanion.App/App.xaml.cs AlbionCompanion.App/Components/Pages/Home.razor
git commit -m "feat(app): render live gathering session state on the home page"
```

---

## Self-Review Notes

- Spec coverage: adapter with exact firing/reset rules from the spec (Task 1); explicit `Attach` call after startup completes, MAUI DI registration, `Home.razor` render + subscribe/unsubscribe lifecycle (Task 2). Non-goals respected: no `IItemDictionaryService` resolution, no session-history UI, no fix to the `Window.Destroying` race (only narrowed by making the attach point explicit, as the spec allows).
- Deviation from the spec's literal file location: `GatheringLiveState` is placed in `AlbionCompanion.Gathering` rather than `AlbionCompanion.App` as the spec's Design section describes. Reason: it has zero MAUI dependency, and placing it in `Gathering` lets it be tested in the existing `AlbionCompanion.Gathering.Tests` xunit project rather than needing a new test project to reference a `Sdk="Microsoft.NET.Sdk.Razor"`/`UseMaui=true` project, which is a real but avoidable complication. This is a file-location change only — the public interface, firing rules, and every behavior in the spec are preserved exactly.
- No placeholders — every step has literal file contents, exact commands, and expected output. The MAUI service-provider resolution question left open in the spec (marked "confirmed at implementation time") is resolved here using `MauiApp.Services`, a standard, stable MAUI API — not template-specific like the TFM/shutdown-hook facts from the prior sub-project that genuinely needed a scaffold probe to confirm.
- Type/name consistency: `IGatheringLiveState` members match between Task 1's definition and Task 2's `Home.razor`/`App.xaml.cs` usage (`IsActive`, `StartLocation`, `TotalFame`, `ItemTotals`, `OnChanged`, `Attach`).
