# AlbionCompanion.App Scaffold (MAUI Blazor Hybrid) — Design

## Problem

The whole gathering/sniffer pipeline (packet capture → Photon parsing → event routing →
persistence → the new domain events from
[2026-07-17-gathering-session-service-events-design.md](2026-07-17-gathering-session-service-events-design.md))
only has a console front-end (`AlbionCompanion.ConsoleHost`) today. The planned UI
(`AlbionCompanion.App`, per `specs/albion-companion-context.md`) doesn't exist as a project yet.
This spec scaffolds that project and gets the existing startup pipeline running inside it, without
building any real screens — that's deferred to later sub-projects (live session view, session
history).

## Goals

- A new `AlbionCompanion.App` project (MAUI Blazor Hybrid, Windows-only target) exists, is added to
  `AlbionCompanion.sln`, and launches as a window.
- On launch, it runs the exact same startup sequence `ConsoleHost` runs today: service
  registration, DB migration, item dictionary seed, raw-event retention sweep, Npcap check, force
  construction of the always-on trackers/loggers, the long-lived session scope
  (`ZoneTracker`/`GatheringEventRouter`/`RawEventRecorder`), and starting the packet sniffer.
- The startup/DI-wiring logic is extracted into a shared helper in `AlbionCompanion.Gathering`, so
  `ConsoleHost` and the new `AlbionCompanion.App` both call the same code instead of each keeping
  their own copy.
- The app shows a single placeholder page — no real data view yet.

## Non-goals

- Any real UI screen (live session view, history view) — later sub-projects.
- Removing `AlbionCompanion.ConsoleHost`. It stays as-is, still fully functional, until the app has
  real screens worth switching to.
- Any change to `GatheringSessionService`, `GatheringEventRouter`, or any other pipeline component's
  behavior — this is a hosting/wiring change only.
- Non-Windows MAUI targets (Android/iOS/Mac Catalyst) — only `net10.0-windows...` is built.

## Design

### New project: `AlbionCompanion.App`

Created via `dotnet new maui-blazor -n AlbionCompanion.App`, Windows-only:
`<TargetFrameworks>net10.0-windows10.0.19041.0</TargetFrameworks>` (matching whatever exact TFM the
installed `maui-windows` workload templates produce — confirmed at scaffold time, not guessed
here). Added to `AlbionCompanion.sln`. Project references: `AlbionCompanion.Core`,
`AlbionCompanion.Gathering`, `AlbionCompanion.Sniffer` (the same three `ConsoleHost` already
references, since it needs the same types to call the shared startup helper).

The default template's sample content (counter page, weather-forecast-style demo components) is
stripped down to a single essentially-empty `MainPage.razor` — enough to confirm the window opens
and the Blazor WebView renders, nothing more.

### Shared startup helper: `AppHostBuilder` (new, in `AlbionCompanion.Gathering`)

`AlbionCompanion.Gathering/AppHostBuilder.cs` — a new static class extracting the entirety of what
`ConsoleHost/Program.cs:62-146` does today into two methods callable from any host:

```csharp
public static class AppHostBuilder
{
    public static ServiceProvider BuildServiceProvider(string appDataPath);
    public static async Task RunStartupSequenceAsync(ServiceProvider provider);
}
```

- `BuildServiceProvider(appDataPath)` does exactly what `Program.cs:62-90` does today: computes the
  `dbPath`/log paths from `appDataPath`, registers every service (`HttpClient`, `INpcapChecker`,
  `NpcapInstaller`, `IPacketSniffer`, `AlbionPhotonParser`/`IPhotonParser`, `AlbionEventLogger`,
  `AlbionEventNameLogger`, `AppDbContext` + factory, `IZoneCatalog`, `ILocalPlayerTracker`,
  `IHarvestableNodeTracker`, `IGatheringSessionService`, `IItemDictionaryService`, `ZoneTracker`,
  `GatheringEventRouter`, `IRawEventRecorder`), and returns the built `ServiceProvider`. Log-file
  paths are derived internally from `appDataPath` using the same filenames `Program.cs` uses today
  (`debug_packets.log`, `debug_event_names.log`, `debug_parse_failures.log`,
  `debug_raw_event_record_failures.log`, `albion.db`) so callers only pass the one directory.
- `RunStartupSequenceAsync(provider)` does exactly what `Program.cs:93-141` does today: the
  migration/seed/retention-sweep block, the Npcap check, force-constructing the always-on
  singletons, creating the long-lived session scope and force-constructing
  `ZoneTracker`/`GatheringEventRouter`/`IRawEventRecorder` in it, wiring `OnRecordFailure` /
  `OnParseFailure` to their log files, wiring `sniffer.OnPhotonPayloadReceived` to
  `photonParser.HandlePayload`, and calling `sniffer.Start()`. It returns the created session
  `IServiceScope` so the caller can `Dispose()` it (and call `sniffer.Stop()`) on shutdown — the
  one piece of lifecycle a console app and a MAUI app necessarily handle differently.

Console-only concerns (`Console.WriteLine` status messages, `Console.ReadLine()` to block, the
`ALBION_DEBUG_PORTS` diagnostic block) stay in `ConsoleHost/Program.cs` — they are presentation,
not startup wiring, and `AlbionCompanion.App` has no console to write to.

### `ConsoleHost/Program.cs` after extraction

Rewritten to call the helper instead of duplicating the logic:

```csharp
// ALBION_DEBUG_PORTS block: unchanged, stays first.

var appDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AlbionCompanion");
Directory.CreateDirectory(appDataPath);

await using var provider = AppHostBuilder.BuildServiceProvider(appDataPath);
var sessionScope = await AppHostBuilder.RunStartupSequenceAsync(provider);

Console.WriteLine("Start Albion Online and go gathering. Press ENTER here to stop.");
Console.ReadLine();

provider.GetRequiredService<IPacketSniffer>().Stop();
sessionScope.Dispose();
Console.WriteLine("Stopped.");
```

The device-list printout (`Program.cs:132-136`) and the two "Recognized event names..."/log-path
status lines stay in `ConsoleHost` too, printed after `RunStartupSequenceAsync` returns (they're
just console status output referencing paths/services already available from `provider`).

### `MauiProgram.cs`

`AlbionCompanion.App/MauiProgram.cs`'s `CreateMauiApp()` builds the `MauiApp` as usual (the
template's `MauiAppBuilder` setup, Blazor WebView registration), then — after the `MauiApp` is
built — calls `AppHostBuilder.BuildServiceProvider(appDataPath)` and
`AppHostBuilder.RunStartupSequenceAsync(provider)` the same way, computing `appDataPath` the same
way `ConsoleHost` does (`Environment.SpecialFolder.ApplicationData` + `"AlbionCompanion"`). The
returned session scope is disposed, and the sniffer stopped, from the MAUI window's shutdown hook
(`Window.Destroying`, wired in `App.xaml.cs` — the exact hook confirmed against the MAUI Windows
lifecycle at implementation time, not guessed here).

The gathering `ServiceProvider` this produces is intentionally separate from MAUI's own DI
container (`MauiProgram`'s `builder.Services`) — the two aren't merged in this spec. A future UI
sub-project that needs to inject `IGatheringSessionService` into a Blazor component will resolve it
from this provider (e.g. via a thin adapter registered into MAUI's container), a wiring detail left
to that sub-project.

## Testing

This is a hosting/wiring scaffold with no new business logic, so there are no new unit tests
beyond what already exists. Verification is:

- `dotnet build` succeeds for the whole solution, including the new `AlbionCompanion.App` project.
- The full existing test suite still passes unchanged (`AppHostBuilder` extraction must not alter
  any behavior `GatheringEventRouterTests`/`RawEventRecorderTests`/etc. depend on).
- Manual smoke test: launch `AlbionCompanion.App`, confirm the window opens, the Npcap check runs,
  and (with Albion Online running) gathering events still get routed and persisted exactly as they
  do today via `ConsoleHost` — confirmed by checking the SQLite DB or the existing debug log files
  after a short play session.
