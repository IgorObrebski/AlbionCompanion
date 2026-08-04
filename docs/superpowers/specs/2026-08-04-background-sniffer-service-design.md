# Background sniffer service — design

## Context

Today, packet capture and gathering-session logic (`IPacketSniffer`, `AlbionPhotonParser`,
`ZoneTracker`, `GatheringEventRouter`, `LocalPlayerTracker`, `IRawEventRecorder`) all run
**inside the `AlbionCompanion.App` MAUI process**, wired up by `AppHostBuilder` and started from
`App.xaml.cs`. This means capture only happens while the UI app is open.

This causes a real gap found 2026-08-04: `ZoneTracker` only starts/ends a gathering session on a
zone-join Photon response (`253:2`) — a signal that fires **on a transition**, not on demand. If
the player is already standing in the open world when the App is launched (game running first,
App started second), no transition happens, so no session ever starts, no matter how long the
player keeps gathering. The only workaround is changing zones after the App is already running.

Separately, closing the App/game without returning to a city leaves a session's `EndTime` null
forever (fixed 2026-08-04 to auto-close on rehydration, see `[[project_feature_backlog]]`) — but
that fix only helps *after* the app relaunches; it doesn't help capture anything that happened
while the app wasn't running at all.

Both problems share one root cause: **capture is tied to the UI process's lifetime.** This design
decouples them: a background Windows Service does all packet capture and gathering-logic
processing continuously, independent of whether `AlbionCompanion.App`'s UI is open. The App
becomes a client: it reads history from the shared database and receives live updates from the
service over a local IPC channel.

## Goals

- Capture works as soon as the machine boots, regardless of the order Albion Online and the App
  are started in.
- The App keeps its existing live-view behavior (toasts, live Broadcast page) with no changes to
  Razor components — only the event *source* changes, from in-process events to IPC.
- The App degrades gracefully (shows history, disables live view) if the service isn't running.
- A user can recover from an accidentally-stopped service without touching Services.msc, PowerShell,
  or Task Manager.

## Non-goals

- Distributing this to other users. Every install-time decision below assumes a single personal
  machine (no MSI/wizard, no code signing, no auto-update).
- Redesigning the gathering/session logic itself — `GatheringSessionService`, `ZoneTracker`,
  `GatheringEventRouter`, `LocalPlayerTracker` move to a new host unchanged.

## Architecture

```
AlbionCompanion.Service (Windows Service, LocalSystem, StartType=Automatic)
  - Worker : BackgroundService, runs AppHostBuilder.BuildServiceProvider + RunStartupSequenceAsync
    (same wiring App.xaml.cs uses today) pointed at %ProgramData%\AlbionCompanion
  - LiveEventPipeServer: named pipe "AlbionCompanionLiveEvents", broadcasts
    IGatheringSessionService's events (OnSessionStarted/Ended/LocationChanged/ItemAdded/
    FameAdded/SilverAdded) to every connected client; also receives CharacterRegistryChanged
    from clients and forwards it to invalidate LocalPlayerTracker's name cache

AlbionCompanion.App (MAUI)
  - No longer hosts IPacketSniffer/AlbionPhotonParser/ZoneTracker/GatheringEventRouter/
    ILocalPlayerTracker/IRawEventRecorder/NpcapInstaller - those move to the Service
  - Keeps IGatheringSessionService (read-side: GetActiveSessionAsync/GetActiveSessionSnapshotAsync),
    ICharacterService (CRUD, used by the Character hub UI), ISessionHistoryService - all reading/
    writing the same %ProgramData%\AlbionCompanion\albion.db
  - New LiveEventPipeClient: connects to the named pipe, feeds IGatheringLiveState from
    deserialized events (mirrors what in-process event subscriptions do today), sends
    CharacterRegistryChanged whenever CharacterService.Add/Delete/RenameAsync succeeds
  - New Settings.razor page: shows service status, manual start button, connection status

AlbionCompanion.ServiceInstaller (new small console exe, run manually, once, elevated)
  - Publishes/copies the Service's self-contained build to %ProgramData%\AlbionCompanion\service\
  - One-time DB/log migration: copies %APPDATA%\AlbionCompanion\* to %ProgramData%\AlbionCompanion\
    if the destination doesn't already have an albion.db
  - Registers the Windows Service (sc create equivalent), sets StartType=Automatic, starts it
  - Grants the current interactive user's SID START/STOP rights on the service via `sc sdset`,
    so Settings.razor's "start" button never needs a UAC prompt
```

## Data flow

**Session start (unchanged internal logic, new transport for the live-update leg):**
`PacketSniffer` → `AlbionPhotonParser` → `ZoneTracker` sees `253:2` → `GatheringSessionService
.StartSessionAsync` (inside the Service process) → DB write + `OnSessionStarted` →
`LiveEventPipeServer` serializes the event, sends to every connected pipe client →
`LiveEventPipeClient` (inside the App process) deserializes it → `IGatheringLiveState` updates →
existing toast/UI logic fires exactly as it does today.

**Rehydration on App launch (the fix for the original bug):**
The App calls `GetActiveSessionSnapshotAsync()` directly against the shared database on startup,
exactly as it does today. Because the Service has been running since boot, a session for the
player's *current* real-world activity already exists with the correct `CharacterId` by the time
the App is opened — there is no more "was I already in the world before the app started" problem,
because capture never depended on the App being open in the first place.

**Character registry changes crossing the process boundary:**
`ICharacterService.Add/Delete/RenameAsync` still runs from the App (Character hub UI writes
directly to the shared DB). After a successful write, the App sends a `CharacterRegistryChanged`
message over the same pipe (client → server direction). `LiveEventPipeServer` relays it to
`LocalPlayerTracker.CharactersChanged` inside the Service process, invalidating its cached
registered-name set (see the 2026-08-04 cache fix in `[[project_feature_backlog]]`) exactly as the
in-process event does today.

**Wire protocol:** newline-delimited JSON over `System.IO.Pipes`. One line per message, tagged
with a discriminator field (`"type"`) matching the six `IGatheringSessionService` events plus
`CharacterRegistryChanged`. No versioning/backward-compatibility handling needed — Service and App
are always deployed together on the same machine by the same installer.

## Storage relocation

`%APPDATA%\AlbionCompanion` is per-user; a service running as `LocalSystem` cannot read it. All
paths (`albion.db`, every `debug_*.log`) move to `%ProgramData%\AlbionCompanion`, writable by
`LocalSystem` and (via default `ProgramData` ACLs) by the interactive user's App process too. The
installer migrates the existing `%APPDATA%\AlbionCompanion\albion.db` there once, preserving
history. `AppHostBuilder.BuildServiceProvider(appDataPath)` already takes `appDataPath` as a
parameter — both hosts just pass a different path, no signature change needed.

**Concurrency:** `AppDbContext`'s SQLite connection string gains `journal_mode=WAL` (one writer +
many readers without `SQLITE_BUSY`) — needed now that Service and App are two OS processes sharing
one file, rather than one process's single `DbContextFactory`.

## Settings page

New `Settings.razor`, linked from `NavMenu` alongside Characters/Sessions.

- Polls `IServiceStatusProvider.GetStatusAsync()` (thin wrapper over `System.ServiceProcess
  .ServiceController`) every few seconds: `Running` (green) / `Stopped` (red) / `NotInstalled`
  (service missing entirely - "run installer.exe again").
- "Start service" button, visible only when status is `Stopped`. Calls
  `ServiceController.Start()` (no UAC prompt, thanks to the installer's `sc sdset` grant) and
  immediately triggers `LiveEventPipeClient`'s connect-now path (see below) instead of waiting for
  its next scheduled retry.
- Connection status to the pipe itself (separate from service process status — the service can be
  `Running` while the pipe hasn't connected yet, e.g. right after a restart): `Connected` /
  `Connecting...` / `Disconnected - see Settings`.

## Client-side retry policy

`LiveEventPipeClient` attempts to connect up to **5 times, 3 seconds apart** (~15s total),
starting when the App launches and whenever a connection drops. After the 5th failure, it stops
retrying automatically and surfaces a persistent "Disconnected" banner (Home/Broadcast pages)
linking to Settings — no infinite silent background loop. Clicking "Start service" in Settings (or
any future manual "retry now" action) resets the attempt counter and immediately makes one
connection attempt rather than waiting for a scheduled retry.

This loop lives entirely inside the App process — it is not itself a persisted/background-service
concept. It starts when the App starts and stops when the App closes; nothing about it needs to
survive an App restart.

## Error handling / degraded mode

- Service not running or pipe never connects: App still serves `Sessions`/`SessionDetail` history
  (pure DB reads, unaffected) and `CharacterHub` (DB CRUD, unaffected). Only the live view
  (Home's active-session card, Broadcast page, session-start toast) is unavailable, with the
  banner described above.
- `ServiceController` throws (service uninstalled, permissions issue despite the `sc sdset` grant):
  caught in `IServiceStatusProvider`, surfaced as `NotInstalled`/an error string in Settings rather
  than propagating and crashing the page.

## Testing

Consistent with this codebase's existing style — real objects over mocks, OS/hardware boundaries
verified manually rather than unit-tested (same treatment `NpcapInstaller` already gets):

**Unit-testable (xUnit, real `System.IO.Pipes` objects in-process, no fakes needed):**
- `LiveEventPipeServer`/`LiveEventPipeClient` round-trip: a real `NamedPipeServerStream` and
  `NamedPipeClientStream` pair in the test process, verifying each of the six session events plus
  `CharacterRegistryChanged` serializes and is received correctly on the other end.
- Retry-cap behavior: a real `NamedPipeClientStream` pointed at a pipe name nobody is listening on
  genuinely fails to connect (no fake needed) — verifies the client gives up after 5 attempts and
  reports exhaustion.
- Reset-and-retry-now: verifies a manual trigger attempts a connection immediately, independent of
  the scheduled interval.
- `Settings.razor`'s status-driven UI logic (when to show the button, which message per status) —
  via a fake `IServiceStatusProvider`, since a real one requires an actual registered Windows
  Service in the SCM. The thin real implementation itself is verified manually.

**Requires manual verification on this machine (like Npcap's installer today):**
- `AlbionCompanion.ServiceInstaller` end-to-end: file copy, `sc create`, `sc sdset` ACL grant,
  `StartType=Automatic` surviving a reboot.
- The original bug scenario: start Albion Online, enter the open world, *then* launch the App —
  confirm a session is already active and correctly attributed.
- WAL mode under real concurrent access: Service and App both touching `albion.db` at once without
  `database is locked`.
- Manually stopping the service (Task Manager / Services.msc) and recovering via the Settings page
  button without a UAC prompt.

## Migration/rollout notes

- First install: existing `%APPDATA%\AlbionCompanion\albion.db` and log files are the only state
  that needs to survive. Everything else (DI wiring, event pipeline) is a lift-and-shift of
  already-tested code into a new host process, not a rewrite.
- `AppHostBuilder.BuildServiceProvider`/`RunStartupSequenceAsync` keep registering the full
  sniffer pipeline exactly as today - `AlbionCompanion.Service`'s `Worker` calls them unchanged
  (just a different `appDataPath`). `AlbionCompanion.App`'s `MauiProgram.cs` stops calling
  `AppHostBuilder` at all; it gets its own new, smaller DI registration (`ICharacterService`,
  `IGatheringSessionService`, `ISessionHistoryService`, `IItemDictionaryService`,
  `LiveEventPipeClient`, `IServiceStatusProvider` - all against the same `%ProgramData%
  \AlbionCompanion\albion.db`), so the App project no longer references `AlbionCompanion.Sniffer`
  at all.
