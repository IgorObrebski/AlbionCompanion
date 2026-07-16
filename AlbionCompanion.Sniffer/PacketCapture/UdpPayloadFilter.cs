using PacketDotNet;

namespace AlbionCompanion.Sniffer.PacketCapture;

public static class UdpPayloadFilter
{
    public const ushort AlbionPortPrimary = 5055;
    public const ushort AlbionPortSecondary = 5056;
    // Observed via ALBION_DEBUG_PORTS: a third fixed server port carrying two-way traffic
    // distinct from 5055/5056 (interaction/gathering-related channel, unconfirmed which).
    public const ushort AlbionPortTertiary = 4535;

    public static bool TryGetAlbionPayload(UdpPacket udpPacket, out byte[] payload)
    {
        var isAlbionTraffic =
            udpPacket.SourcePort == AlbionPortPrimary || udpPacket.DestinationPort == AlbionPortPrimary ||
            udpPacket.SourcePort == AlbionPortSecondary || udpPacket.DestinationPort == AlbionPortSecondary ||
            udpPacket.SourcePort == AlbionPortTertiary || udpPacket.DestinationPort == AlbionPortTertiary;

        if (isAlbionTraffic)
        {
            payload = udpPacket.PayloadData;
            return true;
        }

        payload = Array.Empty<byte>();
        return false;
    }
}
