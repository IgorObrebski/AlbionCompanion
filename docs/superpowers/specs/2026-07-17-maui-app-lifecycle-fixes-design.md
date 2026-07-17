# MAUI App Lifecycle Fixes — Design

## Problem

`AlbionCompanion.App/App.xaml.cs` has two known lifecycle gaps, recorded in project memory
(`project_maui_app_known_issues`) after the MAUI-scaffold sub-project's final review, deliberately
deferred at the time because there was no real UI yet and the process exits almost immediately
around the same window anyway:

1. **Race between `Window.Destroying` and the fire-and-forget startup call.** `StartGatheringAsync()`
   is fired without awaiting from `CreateWindow`. If the window closes before
   `AppHostBuilder.RunStartupSequenceAsync` finishes, `Window.Destroying` runs while
   `MauiProgram.GatheringSessionScope` is still `null` — its `Dispose()` call is skipped, and the
   background task later starts the sniffer and assigns the scope *after* the window is gone, with
   nothing left to tear it down.
2. **Silently-faulted startup task.** A throw inside `RunStartupSequenceAsync` (DB migration
   failure, Npcap install issue) faults an unobserved `Task` — no crash, no log, the MAUI host just
   silently ends up with no gathering pipeline running.

Now that the app has real screens (live session view, session history) depending on the gathering
pipeline actually starting, these gaps are worth closing.

## Goals

- Closing the window can never leave the sniffer running or the session scope undisposed,
  regardless of how close together startup and shutdown happen.
- A startup failure is logged somewhere discoverable, matching how every other failure path in
  this app already logs to a file in `%APPDATA%\AlbionCompanion\`.

## Non-goals

- Retrying startup on failure, or surfacing the failure in the UI itself (e.g., an error banner).
  Logging it is enough for this fix; a UI-visible error state is a separate future concern if it
  turns out to matter in practice.
- Changing `AppHostBuilder.RunStartupSequenceAsync` itself, or any other startup-sequence behavior.
  This spec only changes how `App.xaml.cs` calls and waits on it.

## Design

### Close the race: `Window.Destroying` awaits the startup task

`App.xaml.cs`'s `CreateWindow` keeps a reference to the `Task` returned by `StartGatheringAsync()`
instead of discarding it. `Window.Destroying`'s handler becomes `async void` (acceptable for a
terminal lifecycle event with nothing to return to) and `await`s that task *before* doing any
cleanup:

```csharp
protected override Window CreateWindow(IActivationState? activationState)
{
    var window = new Window(new MainPage()) { Title = "AlbionCompanion" };
    var startupTask = StartGatheringAsync();

    window.Destroying += async (_, _) =>
    {
        await startupTask;
        MauiProgram.GatheringProvider?.GetRequiredService<IPacketSniffer>().Stop();
        MauiProgram.GatheringSessionScope?.Dispose();
    };

    return window;
}
```

Because `StartGatheringAsync` (see below) catches its own exceptions and never lets the task fault,
`await startupTask` here always completes normally — whether startup succeeded or failed, by the
time `Destroying`'s `await` returns, `MauiProgram.GatheringSessionScope` is in its final state
(assigned or still `null` if startup never got that far), and cleanup proceeds correctly either way.
If the window closes before startup finishes, this simply makes shutdown wait the (sub-second)
remainder of startup instead of racing it — an acceptable, bounded delay for a desktop app closing.

### Close the silent fault: catch and log inside `StartGatheringAsync`

`StartGatheringAsync` wraps its body in `try/catch`, logging any exception to a new
`debug_maui_startup_failures.log` file in the same `%APPDATA%\AlbionCompanion\` directory every
other debug log already uses (`debug_packets.log`, `debug_parse_failures.log`,
`debug_raw_event_record_failures.log`), in the same one-line-per-failure format those use.

`MauiProgram` gains a new `public static string? AppDataPath { get; private set; }` property, set
in `CreateMauiApp` at the same point `appDataPath` is already computed — so `App.xaml.cs` can build
the failure-log path without recomputing `Environment.GetFolderPath(...)` itself, and without
`AppHostBuilder` needing to expose its internal log-path scheme (it doesn't today; each host
derives its own paths from a shared `appDataPath` root).

### Explicitly unchanged

`AppHostBuilder.RunStartupSequenceAsync` itself, `MauiProgram.GatheringProvider`/
`GatheringSessionScope`/`Services`, and every other file in the app are untouched. This is a
two-file change (`App.xaml.cs`, `MauiProgram.cs`) confined to startup/shutdown sequencing and
failure logging.

## Testing

This is MAUI lifecycle glue with no plausible way to unit-test `Window.Destroying`/`CreateWindow`
in this repo's existing test setup (no MAUI UI test infrastructure exists, and adding one is out of
scope for a two-file lifecycle fix). Verification is a manual smoke test:

- Launch `AlbionCompanion.App` normally, use it briefly, close the window — confirm clean process
  exit (no hang, no lingering process in Task Manager) as before.
- Launch and close the window immediately (as fast as possible after it appears) — confirm the
  process still exits cleanly rather than leaving an orphaned sniffer/process running. This is the
  specific race window this spec closes; if it can be triggered reliably it's worth trying, but a
  clean exit in the normal-use case is the primary bar given how narrow the window is.
- Temporarily inject a failure into `RunStartupSequenceAsync` (e.g., point `appDataPath` at a
  location without write permission) during manual testing only, confirm
  `debug_maui_startup_failures.log` is created with the exception details, then revert the
  temporary change — this is a manual check, not a permanent test.
