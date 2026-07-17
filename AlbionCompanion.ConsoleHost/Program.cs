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
