using AlbionCompanion.Sniffer.AlbionEvents;
using AlbionCompanion.Sniffer.Npcap;
using AlbionCompanion.Sniffer.PacketCapture;
using AlbionCompanion.Sniffer.Protocol16;
using Microsoft.Extensions.DependencyInjection;

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

Console.WriteLine($"AlbionCompanion Sniffer (debug logging mode). Writing to: {logPath}");
Console.WriteLine("Start Albion Online and go gathering. Press ENTER here to stop.");
sniffer.Start();

Console.ReadLine();

sniffer.Stop();
Console.WriteLine("Stopped.");
