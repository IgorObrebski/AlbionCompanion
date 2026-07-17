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
