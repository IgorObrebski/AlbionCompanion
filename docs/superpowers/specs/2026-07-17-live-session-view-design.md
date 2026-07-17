# Live Session View — Design

## Problem

`AlbionCompanion.App` (scaffolded in the MAUI-scaffold sub-project) shows only a static placeholder
page today. `GatheringSessionService` already raises domain events
(`OnSessionStarted`/`OnSessionEnded`/`OnItemAdded`/`OnFameAdded`, added in a prior sub-project) that
a UI could observe to show the active gathering session live — but nothing subscribes to them yet.
This spec adds that live view: one page showing the current session's collected items and fame as
they happen, without polling the database.

## Goals

- A Blazor page shows: session status (active / ended / no session), the session's start location,
  running total fame, and a per-item breakdown of everything collected so far.
- Updates arrive via the existing domain events, not polling.
- The page keeps showing a just-ended session's final tally until the *next* session starts (so a
  player returning to town sees "here's what I brought back" instead of a blank screen).
- Event handlers are fast and never throw, and never hold onto live EF-tracked entities beyond the
  handler's own scope — per the constraints already recorded from the domain-events sub-project
  (see `project_gathering_events_ui_constraints` memory).

## Non-goals

- Resolving `GatheredItem.ItemId` (a raw `UniqueName` like `"T4_ORE"`) to a display name via
  `IItemDictionaryService`. The raw ID is shown as-is; display-name resolution is a future,
  separate follow-up.
- Session history / past-sessions browsing — a separate sub-project (session history view), sharing
  only the navigation shell this spec introduces.
- Fully closing the `Window.Destroying`-vs-fire-and-forget-startup race recorded in
  `project_maui_app_known_issues` memory. This spec's explicit attach point (see Design) narrows
  the window but does not add a synchronization guard — that fix stays tracked as a separate,
  deferred item.

## Design

### New adapter: `IGatheringLiveState` / `GatheringLiveState`

`AlbionCompanion.App/GatheringLiveState.cs` — registered as a MAUI-DI singleton
(`builder.Services.AddSingleton<IGatheringLiveState, GatheringLiveState>()`), so a Blazor component
can `@inject` it normally instead of reaching into `MauiProgram`'s static state directly.

```csharp
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

- `Attach(sessionService)` subscribes to all four `IGatheringSessionService` events. Called exactly
  once, explicitly, from `App.xaml.cs` right after `AppHostBuilder.RunStartupSequenceAsync`
  completes (see Wiring below) — not via polling for `MauiProgram.GatheringSessionScope` to become
  non-null. This is a deliberate, explicit hookup: the caller knows startup finished because it's
  the code that just awaited it.
- Internally holds a plain `Dictionary<string, int>` for item totals (exposed read-only via the
  interface) — never a reference to the `GatheredItem`/`FameLog` entities themselves. Handlers read
  only the primitive fields they need (`e.ItemId`, `e.Amount`, `e.FameType`, `session.StartLocation`)
  off the event payload and discard the entity reference immediately.
- Every handler body is wrapped in `try/catch` that discards the exception (matching the "must not
  throw" constraint from the domain-events sub-project — a swallowed update is preferable to
  crashing the fire-and-forget dispatch chain that raises these events).

### Firing rules

- **`OnSessionStarted`**: reset `ItemTotals` to empty, `TotalFame` to `0`, `StartLocation` to the
  new session's location, `IsActive = true`. Raises `OnChanged`.
- **`OnSessionEnded`**: `IsActive = false`. `ItemTotals`/`TotalFame`/`StartLocation` are left exactly
  as they were — the just-ended session's final tally stays visible until the next
  `OnSessionStarted` resets it. Raises `OnChanged`.
- **`OnItemAdded`**: `ItemTotals[e.ItemId] = ItemTotals.GetValueOrDefault(e.ItemId) + e.Amount`.
  Raises `OnChanged`.
- **`OnFameAdded`**: `TotalFame += e.Amount`. Raises `OnChanged`.

### Wiring: `App.xaml.cs`

`StartGatheringAsync()` (already present from the MAUI-scaffold sub-project) is extended: after
`AppHostBuilder.RunStartupSequenceAsync` returns the session scope, it resolves
`IGatheringSessionService` from that scope and calls `Attach` on the `IGatheringLiveState` singleton
(resolved from MAUI's `IServiceProvider`, e.g. via `Application.Current.Handler.MauiContext.Services`
or by having `MauiProgram` expose the built `MauiApp`'s service provider — exact resolution
mechanics confirmed at implementation time against however this template version exposes it).

### UI: `Home.razor`

Rewritten to `@inject IGatheringLiveState LiveState`, implement `IDisposable`, subscribe to
`LiveState.OnChanged` in `OnInitialized` (calling `InvokeAsync(StateHasChanged)` on each firing to
marshal onto the Blazor render thread), and unsubscribe in `Dispose`. Renders:

- A status line: "No session" / "Active — {StartLocation}" / "Ended — {StartLocation}" based on
  `IsActive` and whether any data has ever arrived (`StartLocation is null` ⇒ "No session").
- Total fame.
- A table of `ItemTotals`: raw `ItemId` and summed amount, one row per distinct item collected.

### Navigation

This spec adds the first entry to what will become a two-tab shell
(`NavMenu.razor`, currently just a "Home" link left over from the MAUI scaffold): the existing
Home link is repurposed to point at this live view. The second tab (session history, a separate
sub-project) will add its own nav entry when that spec is implemented — this spec doesn't need to
pre-build a nav shell for a page that doesn't exist yet.

## Testing

This logic (event handling, aggregation) is plain C# with no MAUI/Blazor dependency, so it's
directly unit-testable in a new `AlbionCompanion.App.Tests` project (or, if simpler given the
project doesn't have a test project yet, tests added at implementation time in whichever location
matches this repo's existing per-project test-project convention):

- `OnItemAdded` for a new item → appears in `ItemTotals` with the right amount.
- `OnItemAdded` twice for the same item → amounts sum.
- `OnFameAdded` twice → `TotalFame` accumulates.
- `OnSessionStarted` after prior activity → `ItemTotals` empty, `TotalFame` zero, `IsActive` true,
  `StartLocation` updated.
- `OnSessionEnded` → `IsActive` false, `ItemTotals`/`TotalFame`/`StartLocation` unchanged from
  before the end.
- Each of the four handlers raises `OnChanged` exactly once per event.
- A handler that would throw (simulated by a payload provoking an internal error, if one is
  reachable) does not propagate past the `try/catch`.
