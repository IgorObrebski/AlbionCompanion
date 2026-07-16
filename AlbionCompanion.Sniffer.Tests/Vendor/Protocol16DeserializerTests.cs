using Protocol16;
using Xunit;

namespace AlbionCompanion.Sniffer.Tests.Vendor;

// Byte layouts below target Photon "Protocol18" (see Protocol16Deserializer.cs header comment):
// counts are LEB128 varints, CompressedInt is LEB128 + zigzag, and 35 is used as a stand-in for
// a genuinely unrecognized type code (unused by the P18 table: not a defined scalar, not the
// Array marker/bit, not >= the slim-custom-type base).
public class Protocol16DeserializerTests
{
    [Fact]
    public void Deserialize_UnknownTypeCode_ReturnsPlaceholderInsteadOfThrowing()
    {
        var stream = new Protocol16Stream(0);

        var result = Protocol16Deserializer.Deserialize(stream, 35);

        var unsupported = Assert.IsType<UnsupportedPhotonValue>(result);
        Assert.Equal((byte)35, unsupported.TypeCode);
        Assert.Equal("<unsupported Photon type code 35>", unsupported.ToString());
    }

    [Fact]
    public void Deserialize_KnownTypeCode_StillDeserializesNormally()
    {
        var stream = new Protocol16Stream(new byte[] { 0x54 }); // zigzag+LEB128 CompressedInt(42)

        var result = Protocol16Deserializer.Deserialize(stream, 9); // Protocol16Type.CompressedInt

        Assert.Equal(42, result);
    }

    [Fact]
    public void DeserializeEventData_StopsParameterTableAtFirstUnsupportedType_InsteadOfCascadingGarbage()
    {
        // code=9, paramCount=3 (declared), but the 2nd parameter (key=2) has an unrecognized
        // type code (35) with no bytes of its own - so the 3rd declared parameter (key=3,
        // trailing bytes below) must NOT be read, since we can't know where it actually starts.
        var bytes = new byte[]
        {
            9,          // EventData.Code
            3,          // parameter count = 3 (LEB128 varint, single byte)
            1, 9, 0x54, // key=1, type=CompressedInt, value=zigzag(42)
            2, 35,      // key=2, type=35 (unsupported, no value bytes)
            3, 9, 20,   // would-be key=3 entry - must be left unread
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
