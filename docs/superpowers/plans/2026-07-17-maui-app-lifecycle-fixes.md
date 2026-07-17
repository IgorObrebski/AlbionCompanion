# MAUI App Lifecycle Fixes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close the `Window.Destroying`-vs-fire-and-forget-startup race and the silently-faulted startup task in `AlbionCompanion.App/App.xaml.cs`, per the design spec.

**Architecture:** `App.xaml.cs`'s `Window.Destroying` handler awaits the startup task before cleaning up (closing the race); `StartGatheringAsync` catches and logs any startup exception to a new debug log file instead of letting the task fault silently. `MauiProgram.cs` gains an `AppDataPath` property so `App.xaml.cs` can build the new log file's path without recomputing it.

**Tech Stack:** C# / .NET 10, MAUI.

## Global Constraints

- No change to `AppHostBuilder.RunStartupSequenceAsync` or any other startup-sequence behavior — only how `App.xaml.cs` calls and waits on it.
- No UI-visible error state (banner, dialog) is added — a startup failure is logged to a file only, matching every other failure path in this app.
- This is a two-file change (`App.xaml.cs`, `MauiProgram.cs`).

---

### Task 1: Await startup on shutdown, catch and log startup failures

**Files:**
- Modify: `AlbionCompanion.App/MauiProgram.cs`
- Modify: `AlbionCompanion.App/App.xaml.cs`

**Interfaces:**
- Produces: `AlbionCompanion.App.MauiProgram.AppDataPath` (`public static string? { get; private set; }`). Consumed within this same task by `App.xaml.cs` — no later task depends on it.

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
    public static string? AppDataPath { get; private set; }

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

        AppDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AlbionCompanion");
        Directory.CreateDirectory(AppDataPath);
        GatheringProvider = AppHostBuilder.BuildServiceProvider(AppDataPath);

        var app = builder.Build();
        Services = app.Services;
        return app;
    }
}
```

(Only change from the current file: `appDataPath` local variable becomes the `AppDataPath` static
property, used in both places it was previously used as a local.)

The full new `AlbionCompanion.App/App.xaml.cs`:

```csharp
using AlbionCompanion.Gathering;
using AlbionCompanion.Sniffer.PacketCapture;
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
        var startupTask = StartGatheringAsync();

        window.Destroying += async (_, _) =>
        {
            await startupTask;
            MauiProgram.GatheringProvider?.GetRequiredService<IPacketSniffer>().Stop();
            MauiProgram.GatheringSessionScope?.Dispose();
        };

        return window;
    }

    private static async Task StartGatheringAsync()
    {
        if (MauiProgram.GatheringProvider is null)
        {
            return;
        }

        try
        {
            var sessionScope = await AppHostBuilder.RunStartupSequenceAsync(MauiProgram.GatheringProvider);
            MauiProgram.GatheringSessionScope = sessionScope;

            var sessionService = sessionScope.ServiceProvider.GetRequiredService<IGatheringSessionService>();
            MauiProgram.Services?.GetRequiredService<IGatheringLiveState>().Attach(sessionService);
        }
        catch (Exception ex)
        {
            if (MauiProgram.AppDataPath is not null)
            {
                var logPath = Path.Combine(MauiProgram.AppDataPath, "debug_maui_startup_failures.log");
                await File.AppendAllTextAsync(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {ex.GetType().Name}: {ex.Message}{Environment.NewLine}");
            }
        }
    }
}
```

- [ ] **Step 1: Replace `MauiProgram.cs` and `App.xaml.cs` with the exact content above**

- [ ] **Step 2: Build the whole solution**

Run: `dotnet build`
Expected: Build succeeds for every project including `AlbionCompanion.App`.

- [ ] **Step 3: Run the full test suite**

Run: `dotnet test`
Expected: Same pass count as before this change (101 tests, per the most recent prior sub-project's baseline) — this task adds no unit-testable logic (MAUI lifecycle glue with no test infrastructure for it in this repo, per the design spec's Testing section), so no new tests are added here.

- [ ] **Step 4: Manual smoke test — normal use**

Run: `dotnet run --project AlbionCompanion.App`
Expected: Window opens as before (Home/Sessions nav both work). Close the window after a few
seconds of normal use; confirm the process exits cleanly with no hang (the `Window.Destroying`
handler's `await startupTask` should return near-instantly since startup finished long before).

- [ ] **Step 5: Manual smoke test — fast close**

Run: `dotnet run --project AlbionCompanion.App`, and close the window as fast as possible after it
appears (immediately on launch).
Expected: The process still exits cleanly (possibly with a brief, sub-second delay while
`Window.Destroying` awaits the still-in-flight startup task) rather than leaving an orphaned
sniffer process or hanging. This is the specific race window this task closes.

- [ ] **Step 6: Manual smoke test — startup failure logging**

Temporarily break startup to confirm the failure path logs correctly, then revert:
1. In `AlbionCompanion.Gathering/AppHostBuilder.cs`, temporarily add `throw new
   InvalidOperationException("test failure");` as the first line of
   `RunStartupSequenceAsync`.
2. Run `dotnet run --project AlbionCompanion.App`. Expected: window still opens (no crash), and
   `%APPDATA%\AlbionCompanion\debug_maui_startup_failures.log` is created containing a line with
   `InvalidOperationException: test failure`.
3. Revert the temporary throw in `AppHostBuilder.cs` (do not commit it) and confirm `dotnet build`
   is clean again before proceeding.

- [ ] **Step 7: Commit**

```bash
git add AlbionCompanion.App/MauiProgram.cs AlbionCompanion.App/App.xaml.cs
git commit -m "fix(app): close startup/shutdown race and log silently-faulted startup failures"
```

---

## Self-Review Notes

- Spec coverage: `Window.Destroying` now awaits the startup task before cleanup (closes the race);
  `StartGatheringAsync` catches and logs to a new debug log file (closes the silent fault);
  `MauiProgram.AppDataPath` added to support the log path. Both Non-goals respected: no UI-visible
  error state added, `AppHostBuilder` itself untouched.
- No placeholders — full file contents given for both files; the manual verification steps
  (including the temporary-throw-and-revert check) have exact, literal instructions rather than
  vague "verify it works" language.
- Type/name consistency: `MauiProgram.AppDataPath` referenced identically in both files;
  `IPacketSniffer` now has a proper `using AlbionCompanion.Sniffer.PacketCapture;` instead of the
  prior fully-qualified inline reference, consistent with how other types are referenced via
  `using` elsewhere in this file.
