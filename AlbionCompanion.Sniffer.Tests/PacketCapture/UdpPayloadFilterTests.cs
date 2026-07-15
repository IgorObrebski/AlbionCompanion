using AlbionCompanion.Sniffer.PacketCapture;
using PacketDotNet;
using Xunit;

namespace AlbionCompanion.Sniffer.Tests.PacketCapture;

public class UdpPayloadFilterTests
{
    [Theory]
    [InlineData(5055)]
    [InlineData(5056)]
    public void TryGetAlbionPayload_ReturnsTrueWhenDestinationPortIsAlbion(ushort albionPort)
    {
        var udpPacket = new UdpPacket(40000, albionPort) { PayloadData = new byte[] { 1, 2, 3 } };

        var matched = UdpPayloadFilter.TryGetAlbionPayload(udpPacket, out var payload);

        Assert.True(matched);
        Assert.Equal(new byte[] { 1, 2, 3 }, payload);
    }

    [Theory]
    [InlineData(5055)]
    [InlineData(5056)]
    public void TryGetAlbionPayload_ReturnsTrueWhenSourcePortIsAlbion(ushort albionPort)
    {
        var udpPacket = new UdpPacket(albionPort, 40000) { PayloadData = new byte[] { 4, 5 } };

        var matched = UdpPayloadFilter.TryGetAlbionPayload(udpPacket, out var payload);

        Assert.True(matched);
        Assert.Equal(new byte[] { 4, 5 }, payload);
    }

    [Fact]
    public void TryGetAlbionPayload_ReturnsFalseForUnrelatedPorts()
    {
        var udpPacket = new UdpPacket(53, 443) { PayloadData = new byte[] { 9 } };

        var matched = UdpPayloadFilter.TryGetAlbionPayload(udpPacket, out var payload);

        Assert.False(matched);
        Assert.Empty(payload);
    }
}
