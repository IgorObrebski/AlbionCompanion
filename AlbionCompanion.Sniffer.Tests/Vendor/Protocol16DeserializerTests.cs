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

        var unsupported = Assert.IsType<UnsupportedPhotonValue>(result);
        Assert.Equal((byte)153, unsupported.TypeCode);
        Assert.Equal("<unsupported Photon type code 153>", unsupported.ToString());
    }

    [Fact]
    public void Deserialize_KnownTypeCode_StillDeserializesNormally()
    {
        var stream = new Protocol16Stream(new byte[] { 0, 0, 0, 42 }); // big-endian int32 = 42

        var result = Protocol16Deserializer.Deserialize(stream, (byte)'i'); // Protocol16Type.Integer = 105 = 'i'

        Assert.Equal(42, result);
    }

    [Fact]
    public void DeserializeEventData_StopsParameterTableAtFirstUnsupportedType_InsteadOfCascadingGarbage()
    {
        // code=9, paramCount=3 (declared), but the 2nd parameter (key=2) has an unrecognized
        // type code (153) with no bytes of its own - so the 3rd declared parameter (key=3,
        // trailing bytes below) must NOT be read, since we can't know where it actually starts.
        var bytes = new byte[]
        {
            9,          // EventData.Code
            0, 3,       // parameter count = 3 (short, big-endian)
            1, 105, 0, 0, 0, 42, // key=1, type=Integer('i'=105), value=42
            2, 153,     // key=2, type=153 (unsupported, no value bytes)
            3, 105, 0, 0, 0, 99, // would-be key=3 entry - must be left unread
        };
        var stream = new Protocol16Stream(bytes);

        var eventData = Protocol16Deserializer.DeserializeEventData(stream);

        Assert.Equal((byte)9, eventData.Code);
        Assert.Equal(2, eventData.Parameters.Count);
        Assert.Equal(42, eventData.Parameters[1]);
        Assert.IsType<UnsupportedPhotonValue>(eventData.Parameters[2]);
        Assert.False(eventData.Parameters.ContainsKey(3));
    }
}
