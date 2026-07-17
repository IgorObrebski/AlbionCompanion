# GatheringSessionService Domain Events — Design

## Problem

`AlbionCompanion.App` (a future Blazor Hybrid / MAUI UI, planned as a follow-up sub-project) needs
to show a live view of the active gathering session — items collected, fame earned, session
start/end — as they happen. `IGatheringSessionService` today has no way to notify a subscriber
when any of that state changes; the only way to observe it is polling `GetActiveSessionAsync()`
on a timer. This spec adds the domain events a UI (or any other subscriber) needs, without
building the UI itself.

## Goals

- `IGatheringSessionService` exposes events for every state change a live view needs: a session
  starting, a session ending, an item being added, fame being added.
- Events carry the actual data a subscriber needs (the `GatheringSession`/`GatheredItem`/`FameLog`
  instance), not just a signal to re-query.
- Events fire only for real state changes — not for no-ops (duplicate start, ending with no
  active session, adding to a nonexistent session).

## Non-goals

- Building the UI or any subscriber. This spec only adds the events.
- Solving how a long-lived subscriber (e.g. the future UI) obtains the *same*
  `GatheringSessionService` instance that `GatheringEventRouter` writes through, given the service
  is DI-scoped today. That's a wiring concern for the UI scaffolding sub-project, which will need
  to resolve `GatheringSessionService` from the same long-lived scope `Program.cs` already uses
  for `GatheringEventRouter`/`ZoneTracker` (see `Program.cs:111-116`). This spec is unaffected by
  how that's eventually wired.
- Any event for the discarded/empty-session branch of `EndSessionAsync` — see Design below.

## Design

### New events on `IGatheringSessionService`

`AlbionCompanion.Gathering/IGatheringSessionService.cs`:

```csharp
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

### Firing rules (implemented in `GatheringSessionService`)

- **`OnSessionStarted`**: fires after `SaveChangesAsync()`, with the newly created
  `GatheringSession`, only on the branch that actually inserts a new row. `StartSessionAsync`'s
  existing no-op branch (a session is already active) does not fire it.
- **`OnSessionEnded`**: fires after `SaveChangesAsync()`, with the session that was closed
  (`EndTime` set), only on the "real activity, keep the session" branch. Neither the "no active
  session" no-op nor the "empty session, discard it" branch fires it — a discarded session was
  never meaningful to show in a live view, so subscribers don't need to hear about it.
- **`OnItemAdded`**: fires after `SaveChangesAsync()`, with the newly created `GatheredItem`, only
  when there was an active session to attribute it to. The existing "no active session, ignore"
  branch does not fire it.
- **`OnFameAdded`**: same shape as `OnItemAdded`, with the newly created `FameLog`.

All four events are plain `EventHandler<T>` (matching the existing style of
`RawEventRecorder.OnRecordFailure`), raised synchronously right before each method returns.

### Explicitly unchanged

The existing return types, parameters, and persistence behavior of all five interface methods are
unchanged. `GatheringEventRouter`'s calls into `IGatheringSessionService` require no changes — it
remains unaware that the service now raises events.

## Testing

Extend `AlbionCompanion.Gathering.Tests/GatheringSessionServiceTests.cs`:

- `StartSessionAsync` on a clean state → `OnSessionStarted` fires once with the created session.
- `StartSessionAsync` when a session is already active (no-op branch) → `OnSessionStarted` does
  not fire.
- `EndSessionAsync` on a session with activity → `OnSessionEnded` fires once with the session,
  `EndTime` set.
- `EndSessionAsync` on a session with no activity (discarded) → `OnSessionEnded` does not fire.
- `EndSessionAsync` with no active session → `OnSessionEnded` does not fire.
- `AddItemAsync` with an active session → `OnItemAdded` fires once with the created item.
- `AddItemAsync` with no active session (ignored branch) → `OnItemAdded` does not fire.
- `AddFameAsync` with an active session → `OnFameAdded` fires once with the created fame log.
- `AddFameAsync` with no active session (ignored branch) → `OnFameAdded` does not fire.
