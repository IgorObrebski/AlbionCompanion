using AlbionCompanion.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AlbionCompanion.Gathering;

// AlbionCompanion.App's DI wiring - unlike AppHostBuilder (used by AlbionCompanion.Service), this
// never touches IPacketSniffer/AlbionPhotonParser/ZoneTracker/GatheringEventRouter/
// ILocalPlayerTracker/IRawEventRecorder. The App only reads/writes the shared database and talks
// to the Service over LiveEventPipeClient.
public static class AppClientHostBuilder
{
    public static ServiceProvider BuildServiceProvider(string programDataPath)
    {
        var dbPath = Path.Combine(programDataPath, "albion.db");

        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(options => options.UseSqlite($"Data Source={dbPath}"));
        services.AddDbContextFactory<AppDbContext>(options => options.UseSqlite($"Data Source={dbPath}"));
        services.AddSingleton<ICharacterService, CharacterService>();
        services.AddScoped<IGatheringSessionService, GatheringSessionService>();
        services.AddSingleton<IItemDictionaryService, ItemDictionaryService>();
        services.AddSingleton<IServiceStatusProvider, WindowsServiceStatusProvider>();
        services.AddSingleton(_ => new LiveEvents.LiveEventPipeClient("AlbionCompanionLiveEvents"));

        // GatheringSessionService needs an ILocalPlayerTracker to satisfy its constructor even
        // though the App never starts a session itself - a no-op stand-in is enough since
        // StartSessionAsync is never called from this process.
        services.AddSingleton<ILocalPlayerTracker, NullLocalPlayerTracker>();

        return services.BuildServiceProvider();
    }
}

// See AppClientHostBuilder's comment - the App reads GatheringSessionService but never starts
// sessions with it, so this satisfies the constructor dependency without a real Photon connection.
internal class NullLocalPlayerTracker : ILocalPlayerTracker
{
    public int? CurrentEntityId => null;
    public string? CurrentCharacterName => null;
    public event EventHandler<Exception>? OnError { add { } remove { } }
}
