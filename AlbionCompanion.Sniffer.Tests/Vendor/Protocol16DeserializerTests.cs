using Protocol16;
using Xunit;

namespace AlbionCompanion.Sniffer.Tests.Vendor;

public class Protocol16DeserializerTests
{
    [Fact]
    public void Deserialize_UnknownTypeCode_ReturnsPlaceholderInsteadOfThrowing()
    {
        var stream = new Protocol16Stream(0);

        var result = Protocol16Deserializer.Deserialize(stream, 153);

        Assert.Equal("<unsupported Photon type code 153>", result);
    }

    [Fact]
    public void Deserialize_KnownTypeCode_StillDeserializesNormally()
    {
        var stream = new Protocol16Stream(new byte[] { 0, 0, 0, 42 }); // big-endian int32 = 42

        var result = Protocol16Deserializer.Deserialize(stream, (byte)'i'); // Protocol16Type.Integer = 105 = 'i'

        Assert.Equal(42, result);
    }
}
