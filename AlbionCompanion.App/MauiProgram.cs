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
        builder.Services.AddApexCharts();
        builder.Services.AddSingleton<IGatheringLiveState, GatheringLiveState>();
        builder.Services.AddSingleton<ISessionHistoryService>(_ =>
            new SessionHistoryService(GatheringProvider!.GetRequiredService<IDbContextFactory<AppDbContext>>()));
        builder.Services.AddSingleton<IItemDictionaryService>(_ =>
            GatheringProvider!.GetRequiredService<IItemDictionaryService>());
        builder.Services.AddSingleton<ICharacterService>(_ =>
            GatheringProvider!.GetRequiredService<ICharacterService>());

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
