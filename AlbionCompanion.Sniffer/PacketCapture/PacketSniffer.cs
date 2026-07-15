using PacketDotNet;
using SharpPcap;

namespace AlbionCompanion.Sniffer.PacketCapture;

public class PacketSniffer : IPacketSniffer
{
    private const string CaptureFilter = "udp and (port 5055 or port 5056)";

    private readonly List<ILiveDevice> _devices = new();

    public event EventHandler<byte[]>? OnPhotonPayloadReceived;

    public void Start()
    {
        foreach (var device in CaptureDeviceList.Instance)
        {
            device.OnPacketArrival += HandlePacketArrival;
            device.Open(new DeviceConfiguration
            {
                Mode = DeviceModes.Promiscuous,
                ReadTimeout = 1000
            });
            device.Filter = CaptureFilter;
            device.StartCapture();
            _devices.Add(device);
        }
    }

    public void Stop()
    {
        foreach (var device in _devices)
        {
            device.StopCapture();
            device.Close();
            device.OnPacketArrival -= HandlePacketArrival;
        }

        _devices.Clear();
    }

    // Fully-qualified: our own namespace segment "PacketCapture" would otherwise shadow SharpPcap's PacketCapture type.
    private void HandlePacketArrival(object sender, SharpPcap.PacketCapture e)
    {
        var rawCapture = e.GetPacket();
        var packet = rawCapture.GetPacket();
        var udpPacket = packet.Extract<UdpPacket>();

        if (udpPacket is not null && UdpPayloadFilter.TryGetAlbionPayload(udpPacket, out var payload))
        {
            OnPhotonPayloadReceived?.Invoke(this, payload);
        }
    }
}
