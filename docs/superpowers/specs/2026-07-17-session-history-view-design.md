# Session History View — Design

## Problem

`AlbionCompanion.App` shows the currently active gathering session live (see the live-session-view
sub-project) but has no way to browse past sessions. `GatheringSession`/`GatheredItem`/`FameLog`
rows already accumulate in the database for every completed session; this spec adds a page to list
them and a details page to drill into one.

## Goals

- A "Sessions" page lists every completed session: start date/time, location, duration, total fame,
  total items collected — ordered most-recent-first.
- Clicking a session navigates to a details page showing that session's full item breakdown (an
  aggregated table, same shape as the live view's item totals) plus its fame total and location.
- A new query-only service (`ISessionHistoryService`) provides this data; no changes to how
  sessions are written (`GatheringSessionService` is untouched).

## Non-goals

- Showing the currently active (not-yet-ended) session in this list — it's already visible in the
  live view (Home page); duplicating it here would be redundant. The list is strictly
  `EndTime != null` sessions.
- A chronological, event-by-event log view (every individual `GatheredItem`/`FameLog` row with its
  own timestamp). The details page shows an aggregated summary (item → total amount), matching the
  live view's presentation style, not a raw log.
- Editing or deleting sessions from this UI.
- Resolving `GatheredItem.ItemId` to a display name via `IItemDictionaryService` — same deferred
  non-goal as the live-session-view spec; raw `ItemId` is shown as-is.
- Pagination or filtering of the session list. If the list grows large enough to need it, that's a
  future follow-up.

## Design

### New service: `ISessionHistoryService`

`AlbionCompanion.Gathering/ISessionHistoryService.cs` and `SessionHistoryService.cs`:

```csharp
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

- `SessionHistoryService` takes `IDbContextFactory<AppDbContext>` (already registered in
  `AppHostBuilder.BuildServiceProvider`) and creates a short-lived `AppDbContext` per call —
  matching the existing pattern `RawEventRecorder` already uses for the same reason (a query-only
  service has no need to share the long-lived scoped context that `GatheringEventRouter` writes
  through).
- `GetCompletedSessionsAsync`: queries `GatheringSessions.Where(s => s.EndTime != null)`, ordered
  `OrderByDescending(s => s.StartTime)`, projecting `TotalItemsCollected` as
  `s.GatheredItems.Sum(i => i.Amount)` (or `0` for a session with no items). Duration is *not*
  computed in the service — `SessionSummary` carries raw `StartTime`/`EndTime`, and the UI computes
  `EndTime - StartTime` for display, keeping the DTO a plain data carrier.
- `GetSessionDetailAsync(sessionId)`: fetches the single session by `Id` (returns `null` if not
  found or if it has no `EndTime` — an in-progress session has no detail page in this view), then
  groups its `GatheredItems` by `ItemId` summing `Amount` into `ItemTotals`.

### UI: two new pages

`AlbionCompanion.App/Components/Pages/Sessions.razor` (`@page "/sessions"`):
- Injects `ISessionHistoryService`, loads `GetCompletedSessionsAsync()` in `OnInitializedAsync`.
- Renders a table: Start (date/time), Location, Duration (`EndTime - StartTime`, formatted e.g.
  `hh\:mm\:ss`), Total Fame, Total Items. Each row links to `/sessions/{Id}`.
- Empty state ("No completed sessions yet.") when the list is empty.

`AlbionCompanion.App/Components/Pages/SessionDetail.razor` (`@page "/sessions/{SessionId:guid}"`):
- Injects `ISessionHistoryService`, loads `GetSessionDetailAsync(SessionId)` in
  `OnParametersSetAsync` (not `OnInitializedAsync`, so navigating from one session's detail page
  directly to another's — if ever linked that way — reloads correctly).
- Renders the session's location, start/end time, total fame, and a table of `ItemTotals` (same
  `ItemId` → amount shape as the live view's table).
- A "not found" message if `GetSessionDetailAsync` returns `null` (e.g. a stale/bad link), plus a
  link back to `/sessions`.

### Wiring: `MauiProgram.cs`

`ISessionHistoryService` is registered as a MAUI-DI singleton, constructed from the already-built
`GatheringProvider`'s `IDbContextFactory<AppDbContext>` — the same cross-container pattern already
used for `IGatheringLiveState`, except this one needs no `Attach`-style post-startup hookup: the
`IDbContextFactory` registration exists immediately after `BuildServiceProvider` runs, and by the
time a user navigates to the Sessions tab (at least a few seconds into the app's lifetime), the
startup sequence's migration has already completed. No extra readiness guard is added, consistent
with this app's existing philosophy of not special-casing startup-ordering edge cases that aren't
realistically hit (e.g. `RawGatheringEventRetention`'s cleanup sweep also has no special error
handling).

```csharp
builder.Services.AddSingleton<ISessionHistoryService>(_ =>
    new SessionHistoryService(GatheringProvider!.GetRequiredService<IDbContextFactory<AppDbContext>>()));
```

(Exact lambda placement confirmed at implementation time against `MauiProgram.cs`'s current
structure — this is a small, unambiguous addition, not a case needing a scaffold probe like the
MAUI-template facts from an earlier sub-project.)

### Navigation

`NavMenu.razor` gets a second nav entry, "Sessions", pointing at `/sessions`, alongside the existing
"Home" entry.

## Testing

New `AlbionCompanion.Gathering.Tests/SessionHistoryServiceTests.cs`, using the same in-memory SQLite
pattern as the rest of this test project:

- No sessions in the database → `GetCompletedSessionsAsync()` returns an empty list.
- A mix of completed and still-active (`EndTime == null`) sessions → only completed ones are
  returned.
- Multiple completed sessions → returned ordered most-recent-`StartTime`-first.
- A completed session with several `GatheredItems` of different `ItemId`s and amounts →
  `TotalItemsCollected` is the correct sum across all of them.
- `GetSessionDetailAsync` for an existing completed session → returns the correct
  `ItemTotals` aggregation (same-item amounts summed, per the same rule as `GatheringLiveState`).
- `GetSessionDetailAsync` for a nonexistent session ID → returns `null`.
- `GetSessionDetailAsync` for a session that has no `EndTime` (still active) → returns `null` (no
  detail page for an in-progress session in this view).
