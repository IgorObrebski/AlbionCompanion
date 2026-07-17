using AlbionCompanion.Core.Data;
using AlbionCompanion.Gathering;
using AlbionCompanion.Sniffer.AlbionEvents;
using AlbionCompanion.Sniffer.Npcap;
using AlbionCompanion.Sniffer.PacketCapture;
using AlbionCompanion.Sniffer.Protocol16;
using Microsoft.EntityFrameworkCore;
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

var services = new ServiceCollection();

var appDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AlbionCompanion");
Directory.CreateDirectory(appDataPath);
var logPath = Path.Combine(appDataPath, "debug_packets.log");
var eventNamesLogPath = Path.Combine(appDataPath, "debug_event_names.log");
var parseFailuresLogPath = Path.Combine(appDataPath, "debug_parse_failures.log");
var rawEventRecordFailuresLogPath = Path.Combine(appDataPath, "debug_raw_event_record_failures.log");
var dbPath = Path.Combine(appDataPath, "albion.db");

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

await using var provider = services.BuildServiceProvider();

using (var migrationScope = provider.CreateScope())
{
    var dbContext = migrationScope.ServiceProvider.GetRequiredService<AppDbContext>();
    await dbContext.Database.MigrateAsync();
    Console.WriteLine("Checking item dictionary...");
    await migrationScope.ServiceProvider.GetRequiredService<IItemDictionaryService>().SeedFromJsonAsync();

    Console.WriteLine("Cleaning up old raw gathering events...");
    var rawEventCutoff = DateTime.UtcNow - RawGatheringEventRetention.Period;
    await dbContext.RawGatheringEvents
        .Where(e => e.Timestamp < rawEventCutoff)
        .ExecuteDeleteAsync();
}

var npcapInstaller = provider.GetRequiredService<NpcapInstaller>();
Console.WriteLine("Checking Npcap installation...");
await npcapInstaller.EnsureInstalledAsync();

// Force construction so its constructor subscribes to the parser's events before capture starts.
// ZoneTracker is scoped (it holds a scoped AppDbContext transitively via GatheringSessionService),
// so its scope must stay alive for the process lifetime - not disposed until the app exits.
_ = provider.GetRequiredService<AlbionEventLogger>();
_ = provider.GetRequiredService<AlbionEventNameLogger>();
_ = provider.GetRequiredService<ILocalPlayerTracker>();
_ = provider.GetRequiredService<IHarvestableNodeTracker>();
var sessionScope = provider.CreateScope();
_ = sessionScope.ServiceProvider.GetRequiredService<ZoneTracker>();
_ = sessionScope.ServiceProvider.GetRequiredService<GatheringEventRouter>();
var rawEventRecorder = (RawEventRecorder)sessionScope.ServiceProvider.GetRequiredService<IRawEventRecorder>();
rawEventRecorder.OnRecordFailure += (_, ex) =>
    _ = File.AppendAllTextAsync(rawEventRecordFailuresLogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {ex.GetType().Name}: {ex.Message}{Environment.NewLine}");

var photonParser = provider.GetRequiredService<AlbionPhotonParser>();
photonParser.OnParseFailure += (_, ex) =>
    _ = File.AppendAllTextAsync(parseFailuresLogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {ex.GetType().Name}: {ex.Message}{Environment.NewLine}");

var sniffer = provider.GetRequiredService<IPacketSniffer>();
sniffer.OnPhotonPayloadReceived += (_, payload) => photonParser.HandlePayload(payload);

Console.WriteLine("Network devices Npcap can see:");
foreach (var device in SharpPcap.CaptureDeviceList.Instance)
{
    Console.WriteLine($"  - {device.Name} ({device.Description})");
}

Console.WriteLine($"AlbionCompanion Sniffer (debug logging mode). Writing to: {logPath}");
Console.WriteLine($"Recognized event names logged to: {eventNamesLogPath}");
Console.WriteLine("Start Albion Online and go gathering. Press ENTER here to stop.");
sniffer.Start();

Console.ReadLine();

sniffer.Stop();
sessionScope.Dispose();
Console.WriteLine("Stopped.");
