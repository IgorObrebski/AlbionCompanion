using AlbionCompanion.Sniffer.AlbionEvents;
using AlbionCompanion.Sniffer.Npcap;
using AlbionCompanion.Sniffer.PacketCapture;
using AlbionCompanion.Sniffer.Protocol16;
using Microsoft.Extensions.DependencyInjection;
using PacketDotNet;
using SharpPcap;

// TEMPORARY DIAGNOSTIC: set ALBION_DEBUG_PORTS=1 to capture all UDP traffic (no port filter)
// and print each distinct (sourcePort -> destinationPort) pair seen, to find the real ports
// Albion Online uses if 5055/5056 turn out to be stale. Remove once ports are confirmed.
if (Environment.GetEnvironmentVariable("ALBION_DEBUG_PORTS") == "1")
{
    Console.WriteLine("DEBUG MODE: capturing ALL UDP traffic (no port filter). Press ENTER to stop.");
    var seenPortPairs = new HashSet<(ushort Source, ushort Destination)>();
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

            var key = (udpPacket.SourcePort, udpPacket.DestinationPort);
            if (seenPortPairs.Add(key))
            {
                Console.WriteLine($"UDP {udpPacket.SourcePort} -> {udpPacket.DestinationPort} ({udpPacket.PayloadData.Length} bytes) on {device.Name}");
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

services.AddSingleton<HttpClient>();
services.AddSingleton<INpcapChecker, NpcapRegistryChecker>();
services.AddSingleton<NpcapInstaller>();
services.AddSingleton<IPacketSniffer, PacketSniffer>();
services.AddSingleton<IPhotonParser, AlbionPhotonParser>();
services.AddSingleton(sp => new AlbionEventLogger(sp.GetRequiredService<IPhotonParser>(), logPath));

await using var provider = services.BuildServiceProvider();

var npcapInstaller = provider.GetRequiredService<NpcapInstaller>();
Console.WriteLine("Checking Npcap installation...");
await npcapInstaller.EnsureInstalledAsync();

// Force construction so its constructor subscribes to the parser's events before capture starts.
_ = provider.GetRequiredService<AlbionEventLogger>();

var photonParser = provider.GetRequiredService<IPhotonParser>();
var sniffer = provider.GetRequiredService<IPacketSniffer>();
sniffer.OnPhotonPayloadReceived += (_, payload) => photonParser.HandlePayload(payload);

Console.WriteLine("Network devices Npcap can see:");
foreach (var device in SharpPcap.CaptureDeviceList.Instance)
{
    Console.WriteLine($"  - {device.Name} ({device.Description})");
}

Console.WriteLine($"AlbionCompanion Sniffer (debug logging mode). Writing to: {logPath}");
Console.WriteLine("Start Albion Online and go gathering. Press ENTER here to stop.");
sniffer.Start();

Console.ReadLine();

sniffer.Stop();
Console.WriteLine("Stopped.");
