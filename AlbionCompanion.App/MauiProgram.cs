using AlbionCompanion.Core.Data;
using AlbionCompanion.Gathering;
using ApexCharts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AlbionCompanion.App;

public static class MauiProgram
{
    public static ServiceProvider? GatheringProvider { get; private set; }
    public static IServiceScope? GatheringSessionScope { get; set; }
    public static IServiceProvider? Services { get; private set; }
    public static string? ProgramDataPath { get; private set; }

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
        builder.Services.AddApexCharts();
        builder.Services.AddSingleton<IGatheringLiveState, GatheringLiveState>();
        builder.Services.AddSingleton<ISessionHistoryService>(_ =>
            new SessionHistoryService(GatheringProvider!.GetRequiredService<IDbContextFactory<AppDbContext>>()));
        builder.Services.AddSingleton<IItemDictionaryService>(_ =>
            GatheringProvider!.GetRequiredService<IItemDictionaryService>());
        builder.Services.AddSingleton<ICharacterService>(_ =>
            GatheringProvider!.GetRequiredService<ICharacterService>());
        builder.Services.AddSingleton<IServiceStatusProvider>(_ =>
            GatheringProvider!.GetRequiredService<IServiceStatusProvider>());
        builder.Services.AddSingleton(_ =>
            GatheringProvider!.GetRequiredService<AlbionCompanion.Gathering.LiveEvents.LiveEventPipeClient>());

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif

        ProgramDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AlbionCompanion");
        Directory.CreateDirectory(ProgramDataPath);
        GatheringProvider = AppClientHostBuilder.BuildServiceProvider(ProgramDataPath);

        builder.Logging.AddProvider(new FileLoggerProvider(Path.Combine(ProgramDataPath, "debug_app_unhandled_exceptions.log")));

        // Temporary broad-net diagnostic: the App had no way to capture an unhandled exception
        // that surfaces to the Blazor WebView after startup (only App.xaml.cs's own ConnectAsync
        // try/catch was logged) - added 2026-08-04 to catch a live "unhandled error" crash when
        // navigating to Broadcast that had no other visible trace (no Windows Event Log entry,
        // no debug_maui_startup_failures.log). Safe to remove once that's diagnosed.
        var crashLogPath = Path.Combine(ProgramDataPath, "debug_app_unhandled_exceptions.log");
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            LogCrash(crashLogPath, "AppDomain.UnhandledException", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            LogCrash(crashLogPath, "TaskScheduler.UnobservedTaskException", e.Exception);
            e.SetObserved();
        };

        var app = builder.Build();
        Services = app.Services;
        return app;
    }

    private static void LogCrash(string path, string source, Exception? ex)
    {
        try
        {
            File.AppendAllText(path,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {source}: {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Logging the crash must never itself throw.
        }
    }
}
