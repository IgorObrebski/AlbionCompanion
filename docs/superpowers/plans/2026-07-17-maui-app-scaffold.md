# AlbionCompanion.App Scaffold Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Scaffold a new `AlbionCompanion.App` (MAUI Blazor Hybrid, Windows-only) project that runs the exact same startup pipeline `AlbionCompanion.ConsoleHost` runs today, via a shared `AppHostBuilder` helper extracted out of `ConsoleHost`, with no real UI screens yet.

**Architecture:** Task 1 extracts `ConsoleHost/Program.cs`'s DI-wiring/startup-sequence logic into a new `AppHostBuilder` static class in `AlbionCompanion.Gathering`, with `ConsoleHost` refactored to call it (behavior-preserving refactor, verified by the existing test suite passing unchanged). Task 2 scaffolds the new MAUI project via the `dotnet new maui-blazor` template, strips its sample content down to a placeholder page, and wires `MauiProgram.cs`/`App.xaml.cs` to call the same `AppHostBuilder` methods, with the session scope disposed on the MAUI window's `Destroying` event.

**Tech Stack:** C# / .NET 10, EF Core (SQLite), MAUI Blazor Hybrid (Windows/WinUI target), xUnit.

## Global Constraints

- No change to `GatheringSessionService`, `GatheringEventRouter`, or any pipeline component's behavior — this is hosting/wiring only.
- `AlbionCompanion.ConsoleHost` stays fully functional and is not removed.
- Only the Windows MAUI target is built (`net10.0-windows10.0.19041.0`) — no Android/iOS/MacCatalyst.
- The startup sequence order must stay identical to today's `ConsoleHost/Program.cs`: register services → migrate DB → seed item dictionary → raw-event retention sweep → Npcap check → force-construct always-on singletons → create long-lived session scope → force-construct `ZoneTracker`/`GatheringEventRouter`/`RawEventRecorder` in it → wire failure-log handlers → wire sniffer→parser → `sniffer.Start()`.

---

### Task 1: Extract `AppHostBuilder` and refactor `ConsoleHost` to use it

**Files:**
- Create: `AlbionCompanion.Gathering/AppHostBuilder.cs`
- Modify: `AlbionCompanion.ConsoleHost/Program.cs`

**Interfaces:**
- Produces: `AlbionCompanion.Gathering.AppHostBuilder.BuildServiceProvider(string appDataPath)` → `ServiceProvider`; `AlbionCompanion.Gathering.AppHostBuilder.RunStartupSequenceAsync(ServiceProvider provider)` → `Task<IServiceScope>`. Consumed by Task 2's `MauiProgram.cs`.
- Consumes: nothing new — reuses `IGatheringSessionService`, `IItemDictionaryService`, `RawGatheringEventRetention`, `IRawEventRecorder`/`RawEventRecorder`, `ZoneTracker`, `GatheringEventRouter` (all already in `AlbionCompanion.Gathering`), and `AlbionCompanion.Sniffer` types, exactly as `ConsoleHost/Program.cs` does today.

This task is a behavior-preserving refactor: every line of DI-wiring/startup logic moves, none of it changes. No new tests are added — correctness is verified by the existing test suite passing unchanged, plus a manual build/run check of `ConsoleHost`.

The full new `AlbionCompanion.Gathering/AppHostBuilder.cs`:

```csharp
using AlbionCompanion.Core.Data;
using AlbionCompanion.Sniffer.AlbionEvents;
using AlbionCompanion.Sniffer.Npcap;
using AlbionCompanion.Sniffer.PacketCapture;
using AlbionCompanion.Sniffer.Protocol16;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AlbionCompanion.Gathering;

// Shared startup wiring for every host (ConsoleHost today, AlbionCompanion.App going forward).
// Extracted so both hosts run the identical DI registration and startup sequence instead of each
// keeping their own copy - see docs/superpowers/specs/2026-07-17-maui-app-scaffold-design.md.
public static class AppHostBuilder
{
    public static ServiceProvider BuildServiceProvider(string appDataPath)
    {
        var logPath = Path.Combine(appDataPath, "debug_packets.log");
        var eventNamesLogPath = Path.Combine(appDataPath, "debug_event_names.log");
        var parseFailuresLogPath = Path.Combine(appDataPath, "debug_parse_failures.log");
        var rawEventRecordFailuresLogPath = Path.Combine(appDataPath, "debug_raw_event_record_failures.log");
        var dbPath = Path.Combine(appDataPath, "albion.db");

        var services = new ServiceCollection();

        services.AddSingleton<HttpClient>();
        services.AddSingleton<INpcapChecker, NpcapRegistryChecker>();
        services.AddSingleton<NpcapInstaller>();
        services.AddSingleton<IPacketSniffer, PacketSniffer>();
        services.AddSingleton<AlbionPhotonParser>();
        services.AddSingleton<IPhotonParser>(sp => sp.GetRequiredService<AlbionPhotonParser>());
        services.AddSingleton(sp => new AlbionEventLogger(sp.GetRequiredService<IPhotonParser>(), logPath));
        services.AddSingleton(sp => new AlbionEventNameLogger(sp.GetRequiredService<IPhotonParser>(), eventNamesLogPath));
        services.AddDbContext<AppDbContext>(options => options.UseSqlite($"Data Source={dbPath}"));
        services.AddDbContextFactory<AppDbContext>(options => options.UseSqlite($"Data Source={dbPath}"));
        services.AddSingleton<IZoneCatalog, ZoneCatalog>();
        services.AddSingleton<ILocalPlayerTracker, LocalPlayerTracker>();
        services.AddSingleton<IHarvestableNodeTracker, HarvestableNodeTracker>();
        services.AddScoped<IGatheringSessionService, GatheringSessionService>();
        services.AddScoped<IItemDictionaryService, ItemDictionaryService>();
        services.AddScoped<ZoneTracker>();
        services.AddScoped<GatheringEventRouter>();
        services.AddScoped<IRawEventRecorder, RawEventRecorder>();

        // Stashed as singletons purely so RunStartupSequenceAsync can reach them without
        // recomputing from appDataPath - these are file paths, not services to inject elsewhere.
        services.AddSingleton(new HostLogPaths(parseFailuresLogPath, rawEventRecordFailuresLogPath));

        return services.BuildServiceProvider();
    }

    public static async Task<IServiceScope> RunStartupSequenceAsync(ServiceProvider provider)
    {
        using (var migrationScope = provider.CreateScope())
        {
            var dbContext = migrationScope.ServiceProvider.GetRequiredService<AppDbContext>();
            await dbContext.Database.MigrateAsync();
            await migrationScope.ServiceProvider.GetRequiredService<IItemDictionaryService>().SeedFromJsonAsync();

            var rawEventCutoff = DateTime.UtcNow - RawGatheringEventRetention.Period;
            await dbContext.RawGatheringEvents
                .Where(e => e.Timestamp < rawEventCutoff)
                .ExecuteDeleteAsync();
        }

        var npcapInstaller = provider.GetRequiredService<NpcapInstaller>();
        await npcapInstaller.EnsureInstalledAsync();

        // Force construction so its constructor subscribes to the parser's events before capture
        // starts. ZoneTracker is scoped (it holds a scoped AppDbContext transitively via
        // GatheringSessionService), so its scope must stay alive for the process/app lifetime -
        // not disposed until the host shuts down.
        _ = provider.GetRequiredService<AlbionEventLogger>();
        _ = provider.GetRequiredService<AlbionEventNameLogger>();
        _ = provider.GetRequiredService<ILocalPlayerTracker>();
        _ = provider.GetRequiredService<IHarvestableNodeTracker>();

        var sessionScope = provider.CreateScope();
        _ = sessionScope.ServiceProvider.GetRequiredService<ZoneTracker>();
        _ = sessionScope.ServiceProvider.GetRequiredService<GatheringEventRouter>();

        var logPaths = provider.GetRequiredService<HostLogPaths>();
        var rawEventRecorder = (RawEventRecorder)sessionScope.ServiceProvider.GetRequiredService<IRawEventRecorder>();
        rawEventRecorder.OnRecordFailure += (_, ex) =>
            _ = File.AppendAllTextAsync(logPaths.RawEventRecordFailuresLogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {ex.GetType().Name}: {ex.Message}{Environment.NewLine}");

        var photonParser = provider.GetRequiredService<AlbionPhotonParser>();
        photonParser.OnParseFailure += (_, ex) =>
            _ = File.AppendAllTextAsync(logPaths.ParseFailuresLogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {ex.GetType().Name}: {ex.Message}{Environment.NewLine}");

        var sniffer = provider.GetRequiredService<IPacketSniffer>();
        sniffer.OnPhotonPayloadReceived += (_, payload) => photonParser.HandlePayload(payload);

        sniffer.Start();

        return sessionScope;
    }

    private sealed record HostLogPaths(string ParseFailuresLogPath, string RawEventRecordFailuresLogPath);
}
```

- [ ] **Step 1: Confirm the current full test suite passes before refactoring (baseline)**

Run: `dotnet test`
Expected: All tests PASS (this is the pre-refactor baseline — record the pass count to compare after the refactor).

- [ ] **Step 2: Create `AppHostBuilder.cs` with the exact content above**

- [ ] **Step 3: Replace `AlbionCompanion.ConsoleHost/Program.cs` with the following**

```csharp
using AlbionCompanion.Gathering;
using AlbionCompanion.Sniffer.PacketCapture;
using Microsoft.Extensions.DependencyInjection;
using PacketDotNet;
using SharpPcap;

// TEMPORARY DIAGNOSTIC: set ALBION_DEBUG_PORTS=1 to capture all UDP traffic (no port filter)
// and log every (sourcePort -> destinationPort) pair with a running packet count and timestamp
// of last activity, to see which port lights up specifically during a gathering action if
// 5055/5056 turn out to only carry movement traffic. Remove once ports are confirmed.
if (Environment.GetEnvironmentVariable("ALBION_DEBUG_PORTS") == "1")
{
    var debugLogPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AlbionCompanion", "debug_ports.log");
    Directory.CreateDirectory(Path.GetDirectoryName(debugLogPath)!);
    Console.WriteLine("DEBUG MODE: capturing ALL UDP traffic (no port filter). Press ENTER to stop.");
    Console.WriteLine($"Writing port activity to: {debugLogPath}");
    var packetCounts = new Dictionary<(ushort Source, ushort Destination), int>();
    var debugDevices = new List<ILiveDevice>();

    foreach (var device in CaptureDeviceList.Instance)
    {
        device.OnPacketArrival += (_, e) =>
        {
            var udpPacket = e.GetPacket().GetPacket().Extract<UdpPacket>();
            if (udpPacket is null)
            {
                return;
            }

            (ushort Source, ushort Destination) key = (udpPacket.SourcePort, udpPacket.DestinationPort);
            packetCounts[key] = packetCounts.GetValueOrDefault(key) + 1;
            var count = packetCounts[key];
            if (count == 1 || count % 100 == 0)
            {
                var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] UDP {key.Source} -> {key.Destination} ({udpPacket.PayloadData.Length} bytes) count={count} on {device.Name}";
                Console.WriteLine(line);
                _ = File.AppendAllTextAsync(debugLogPath, line + Environment.NewLine);
            }
        };
        device.Open(new DeviceConfiguration { Mode = DeviceModes.Promiscuous, ReadTimeout = 1000 });
        device.Filter = "udp";
        device.StartCapture();
        debugDevices.Add(device);
    }

    Console.ReadLine();

    foreach (var device in debugDevices)
    {
        device.StopCapture();
        device.Close();
    }

    return;
}

var appDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AlbionCompanion");
Directory.CreateDirectory(appDataPath);

Console.WriteLine("Checking item dictionary...");
Console.WriteLine("Cleaning up old raw gathering events...");
Console.WriteLine("Checking Npcap installation...");

await using var provider = AppHostBuilder.BuildServiceProvider(appDataPath);
var sessionScope = await AppHostBuilder.RunStartupSequenceAsync(provider);

Console.WriteLine("Network devices Npcap can see:");
foreach (var device in SharpPcap.CaptureDeviceList.Instance)
{
    Console.WriteLine($"  - {device.Name} ({device.Description})");
}

var logPath = Path.Combine(appDataPath, "debug_packets.log");
var eventNamesLogPath = Path.Combine(appDataPath, "debug_event_names.log");
Console.WriteLine($"AlbionCompanion Sniffer (debug logging mode). Writing to: {logPath}");
Console.WriteLine($"Recognized event names logged to: {eventNamesLogPath}");
Console.WriteLine("Start Albion Online and go gathering. Press ENTER here to stop.");

Console.ReadLine();

var sniffer = provider.GetRequiredService<IPacketSniffer>();
sniffer.Stop();
sessionScope.Dispose();
Console.WriteLine("Stopped.");
```

Note: the status `Console.WriteLine` calls that used to happen *during* the startup sequence
("Checking item dictionary...", "Cleaning up old raw gathering events...", "Checking Npcap
installation...") now print *before* calling `RunStartupSequenceAsync` instead of interleaved with
it, since that logic moved into the console-agnostic helper. This is an acceptable, documented
behavior difference (console output ordering/timing only — no functional change), not a deviation
from the Global Constraints (which govern the *startup sequence itself*, not console log
placement).

- [ ] **Step 4: Build and run the full test suite**

Run: `dotnet build && dotnet test`
Expected: Build succeeds. Every test that passed in Step 1's baseline still passes — no new tests were added (this is a pure refactor), so the pass count must match exactly.

- [ ] **Step 5: Manual smoke test of `ConsoleHost`**

Run: `dotnet run --project AlbionCompanion.ConsoleHost`
Expected: Console prints the same sequence of status lines as before (item dictionary check, npcap check, device list, "Start Albion Online..."), waits on `Console.ReadLine()`. Press ENTER to confirm clean shutdown ("Stopped."). This confirms the extraction didn't silently break the runtime wiring in a way the test suite wouldn't catch (test suite doesn't cover `Program.cs` itself, since it has no unit tests today either).

- [ ] **Step 6: Commit**

```bash
git add AlbionCompanion.Gathering/AppHostBuilder.cs AlbionCompanion.ConsoleHost/Program.cs
git commit -m "refactor(gathering): extract shared AppHostBuilder from ConsoleHost startup"
```

---

### Task 2: Scaffold `AlbionCompanion.App` and wire it to `AppHostBuilder`

**Files:**
- Create: `AlbionCompanion.App/` (via `dotnet new maui-blazor`, then modified — exact file list below)
- Modify: `AlbionCompanion.sln`

**Interfaces:**
- Consumes: `AlbionCompanion.Gathering.AppHostBuilder.BuildServiceProvider(string)` and `.RunStartupSequenceAsync(ServiceProvider)` (Task 1).
- Produces: nothing consumed by a later task in this plan — this is the final task.

- [ ] **Step 1: Scaffold the project**

Run, from the repo root:

```bash
dotnet new maui-blazor -n AlbionCompanion.App
```

This creates `AlbionCompanion.App/` with the template's default file set (`MauiProgram.cs`,
`App.xaml`/`App.xaml.cs`, `MainPage.xaml`/`MainPage.xaml.cs`, `Components/Pages/Home.razor`,
`Components/Pages/Counter.razor`, `Components/Pages/Weather.razor`, `Components/Layout/*`,
`Platforms/*`, `Resources/*`, `wwwroot/*`, `AlbionCompanion.App.csproj`).

- [ ] **Step 2: Restrict the project to the Windows target only**

Edit `AlbionCompanion.App/AlbionCompanion.App.csproj` — the generated file has two
`<TargetFrameworks>` lines near the top:

```xml
<TargetFrameworks>net10.0-android;net10.0-ios;net10.0-maccatalyst</TargetFrameworks>
<TargetFrameworks Condition="$([MSBuild]::IsOSPlatform('windows'))">$(TargetFrameworks);net10.0-windows10.0.19041.0</TargetFrameworks>
```

Replace both lines with a single line:

```xml
<TargetFrameworks>net10.0-windows10.0.19041.0</TargetFrameworks>
```

(Only the Windows workload — `maui-windows` — is installed on this machine, and the design's
Non-goals explicitly exclude the other platforms.)

- [ ] **Step 3: Add project references**

Add to `AlbionCompanion.App.csproj`'s existing `<ItemGroup>` that already contains the
`Microsoft.Maui.Controls`/`Microsoft.AspNetCore.Components.WebView.Maui`/
`Microsoft.Extensions.Logging.Debug` `PackageReference`s (add a new `<ItemGroup>` if cleaner):

```xml
<ItemGroup>
    <ProjectReference Include="..\AlbionCompanion.Core\AlbionCompanion.Core.csproj" />
    <ProjectReference Include="..\AlbionCompanion.Gathering\AlbionCompanion.Gathering.csproj" />
    <ProjectReference Include="..\AlbionCompanion.Sniffer\AlbionCompanion.Sniffer.csproj" />
</ItemGroup>
```

- [ ] **Step 4: Add the project to the solution**

Run: `dotnet sln AlbionCompanion.sln add AlbionCompanion.App/AlbionCompanion.App.csproj`

- [ ] **Step 5: Strip the sample pages down to one placeholder**

Delete `AlbionCompanion.App/Components/Pages/Counter.razor` and
`AlbionCompanion.App/Components/Pages/Weather.razor` (sample demo pages, not needed).

Replace the contents of `AlbionCompanion.App/Components/Pages/Home.razor` with:

```razor
@page "/"

<h1>AlbionCompanion</h1>

<p>Sniffer host running.</p>
```

Remove the now-dangling nav links to Counter/Weather from
`AlbionCompanion.App/Components/Layout/NavMenu.razor` (delete the `<div class="nav-item">` blocks
whose `NavLink` targets `counter` and `weather`, keeping the one for `""`/Home).

- [ ] **Step 6: Wire `MauiProgram.cs` to `AppHostBuilder`**

Replace the contents of `AlbionCompanion.App/MauiProgram.cs` with:

```csharp
using AlbionCompanion.Gathering;
using Microsoft.Extensions.Logging;

namespace AlbionCompanion.App;

public static class MauiProgram
{
    public static ServiceProvider? GatheringProvider { get; private set; }
    public static IServiceScope? GatheringSessionScope { get; private set; }

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

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif

        var appDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AlbionCompanion");
        Directory.CreateDirectory(appDataPath);
        GatheringProvider = AppHostBuilder.BuildServiceProvider(appDataPath);

        return builder.Build();
    }
}
```

`GatheringSessionScope` is populated in Step 7 (from `App.xaml.cs`, which needs to run the
startup sequence *after* the `MauiApp` is built, per the design's "after the `MauiApp` is built"
sequencing) and disposed on window shutdown in the same step.

- [ ] **Step 7: Wire `App.xaml.cs` to run the startup sequence and dispose it on shutdown**

Replace the contents of `AlbionCompanion.App/App.xaml.cs` with:

```csharp
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

        MauiProgram.GatheringSessionScope = await AppHostBuilder.RunStartupSequenceAsync(MauiProgram.GatheringProvider);
    }
}
```

Add `using AlbionCompanion.Gathering;` and `using Microsoft.Extensions.DependencyInjection;` to the
top of this file.

- [ ] **Step 8: Build the whole solution**

Run: `dotnet build`
Expected: Build succeeds for every project including `AlbionCompanion.App`
(`net10.0-windows10.0.19041.0`).

- [ ] **Step 9: Run the full test suite (confirm no regressions)**

Run: `dotnet test`
Expected: Same pass count as Task 1's Step 4 — this task adds no unit-testable logic of its own
(pure app scaffolding + wiring), so no new tests are added here.

- [ ] **Step 10: Manual smoke test**

Run: `dotnet run --project AlbionCompanion.App`
Expected: A window titled "AlbionCompanion" opens showing "AlbionCompanion" / "Sniffer host
running." The Npcap check and DB migration run in the background (no console to show their
status in this host — that's expected, per the design). Close the window; confirm the process
exits cleanly (no hang, no unhandled exception in the debug output).

- [ ] **Step 11: Commit**

```bash
git add AlbionCompanion.App AlbionCompanion.sln
git commit -m "feat(app): scaffold AlbionCompanion.App MAUI Blazor Hybrid host"
```

---

## Self-Review Notes

- Spec coverage: shared `AppHostBuilder` in `AlbionCompanion.Gathering` (Task 1) with the exact two
  methods and signatures from the spec; `ConsoleHost` refactored to call it, unchanged behavior
  (Task 1); new `AlbionCompanion.App` project, Windows-only TFM, added to the `.sln`, references
  to `Core`/`Gathering`/`Sniffer`, sample pages stripped to one placeholder, `MauiProgram.cs` +
  `App.xaml.cs` wired to the helper, session scope disposed on `Window.Destroying` (Task 2). All
  Non-goals respected: no real UI screen built, `ConsoleHost` not removed, no other MAUI platform
  targeted, no change to `GatheringSessionService`/`GatheringEventRouter` behavior.
- No placeholders — every step has literal file contents, exact commands, and expected output.
  The one previously-open question from the design spec (exact TFM, exact MAUI shutdown hook) was
  resolved by actually scaffolding a probe project during plan-writing: confirmed
  `net10.0-windows10.0.19041.0` and `Window.Destroying` are real, current facts about this
  template/workload version, not assumptions.
- Type/name consistency: `AppHostBuilder.BuildServiceProvider`/`RunStartupSequenceAsync` signatures
  match between Task 1 (where they're defined) and Task 2 (where `MauiProgram.cs`/`App.xaml.cs`
  call them). `MauiProgram.GatheringProvider`/`GatheringSessionScope` names match between the two
  files in Task 2.
