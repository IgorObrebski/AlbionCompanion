using PacketDotNet;

namespace AlbionCompanion.Sniffer.PacketCapture;

public static class UdpPayloadFilter
{
    public const ushort AlbionPortPrimary = 5055;
    public const ushort AlbionPortSecondary = 5056;

    public static bool TryGetAlbionPayload(UdpPacket udpPacket, out byte[] payload)
    {
        var isAlbionTraffic =
            udpPacket.SourcePort == AlbionPortPrimary || udpPacket.DestinationPort == AlbionPortPrimary ||
            udpPacket.SourcePort == AlbionPortSecondary || udpPacket.DestinationPort == AlbionPortSecondary;

        if (isAlbionTraffic)
        {
            payload = udpPacket.PayloadData;
            return true;
        }

        payload = Array.Empty<byte>();
        return false;
    }
}
