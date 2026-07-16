// Originally vendored from https://github.com/0blu/PhotonPackageParser (MIT) - see THIRD-PARTY-NOTICES.md
// REPLACED with the Photon "Protocol18" type table: Albion Online switched from Protocol16 to
// Protocol18 in the 2026-04-13 game patch, and the old Protocol16 byte values below no longer
// match the wire format at all. Values ported from Nouuu/Albion-Online-OpenRadar (MIT),
// internal/photon/typecodes.go, which is actively kept in sync with the live game protocol.
namespace Protocol16
{
    internal enum Protocol16Type : byte
    {
        Unknown = 0,
        Boolean = 2,
        Byte = 3,
        Short = 4,
        Float = 5,
        Double = 6,
        String = 7,
        Null = 8,
        CompressedInt = 9,
        CompressedLong = 10,
        Int1 = 11,
        Int1Neg = 12,
        Int2 = 13,
        Int2Neg = 14,
        Long1 = 15,
        Long1Neg = 16,
        Long2 = 17,
        Long2Neg = 18,
        Custom = 19,
        Dictionary = 20,
        Hashtable = 21,
        ObjectArray = 23,
        OperationRequest = 24,
        OperationResponse = 25,
        EventData = 26,
        BoolFalse = 27,
        BoolTrue = 28,
        ShortZero = 29,
        IntZero = 30,
        LongZero = 31,
        FloatZero = 32,
        DoubleZero = 33,
        ByteZero = 34,
        // Not a discrete value: ORed with an element type code to mark a typed array (see
        // Deserialize's dispatch). Anything >= CustomTypeSlimBase is a "slim" custom type whose
        // payload is [compressed length][raw bytes] with no separate customTypeId byte.
        Array = 0x40,
        CustomTypeSlimBase = 0x80,
    }
}
