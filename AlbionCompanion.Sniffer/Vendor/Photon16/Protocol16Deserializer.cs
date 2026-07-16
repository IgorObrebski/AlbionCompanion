// Originally vendored from https://github.com/0blu/PhotonPackageParser (MIT) - see THIRD-PARTY-NOTICES.md
// REWRITTEN for Photon "Protocol18": Albion Online switched from Protocol16 to Protocol18 in the
// 2026-04-13 game patch, and the old byte-for-byte Protocol16 decoding here silently produced
// garbage/truncated parses against live traffic (confirmed via debug_packets.log captures: only
// the simplest events parsed, everything else hit "unsupported type" almost immediately).
// Deserialization logic ported from Nouuu/Albion-Online-OpenRadar (MIT), internal/photon/
// {deserializer.go, readers.go, typecodes.go}, which is actively kept in sync with the live
// game protocol. Key format differences from Protocol16:
//   - Parameter/dictionary/array counts and string lengths are LEB128 "compressed" varints
//     instead of a fixed 2-byte short.
//   - CompressedInt/CompressedLong use LEB128 + zigzag encoding.
//   - Several dedicated "zero" types (ShortZero, IntZero, ...) encode common values with zero
//     payload bytes; Int1/Int2/Long1/Long2 (+ Neg variants) encode small magnitudes in 1-2 bytes.
//   - Multi-byte fixed values (Short/Float/Double) are little-endian on the wire (Protocol16 was
//     big-endian), which happens to match .NET's native byte order - no manual swap needed.
//   - Custom types >= 0x80 ("slim") carry [compressed length][raw bytes] with no separate
//     type-id byte, unlike the legacy Custom(19) marker which still has one. Because every type
//     now has a computable byte length, deserialization no longer needs to abort the enclosing
//     parameter table early on an unrecognized value (the old UnsupportedPhotonValue marker) -
//     only a genuinely unknown type code still does.
#pragma warning disable CA2022 // Protocol16Stream.Read is a plain in-memory buffer copy, not a network/file stream.

using System;
using System.Collections.Generic;
using System.Text;
using Protocol16.Photon;

namespace Protocol16
{
    /// <summary>Marks a Photon parameter type code this deserializer doesn't recognize at all.</summary>
    public sealed class UnsupportedPhotonValue
    {
        public byte TypeCode { get; }

        public UnsupportedPhotonValue(byte typeCode) => TypeCode = typeCode;

        public override string ToString() => $"<unsupported Photon type code {TypeCode}>";
    }

    public static class Protocol16Deserializer
    {
        // Mirrors OpenRadar's maxArraySize: a sanity cap on any wire-supplied count, so a
        // corrupt/misaligned read can't trigger a huge allocation or a runaway loop.
        private const int MaxArraySize = 65536;

        #region public API

        public static object? Deserialize(Protocol16Stream input, byte typeCode)
        {
            if (typeCode >= (byte)Protocol16Type.CustomTypeSlimBase)
            {
                return DeserializeCustom(input, typeCode);
            }

            switch ((Protocol16Type)typeCode)
            {
                case Protocol16Type.Unknown:
                case Protocol16Type.Null:
                    return null;
                case Protocol16Type.Boolean:
                    return DeserializeByte(input) != 0;
                case Protocol16Type.Byte:
                    return DeserializeByte(input);
                case Protocol16Type.Short:
                    return DeserializeShort(input);
                case Protocol16Type.Float:
                    return DeserializeFloat(input);
                case Protocol16Type.Double:
                    return DeserializeDouble(input);
                case Protocol16Type.String:
                    return DeserializeString(input);
                case Protocol16Type.CompressedInt:
                    return DeserializeCompressedInt32(input);
                case Protocol16Type.CompressedLong:
                    return DeserializeCompressedInt64(input);
                case Protocol16Type.Int1:
                    return (int)DeserializeByte(input);
                case Protocol16Type.Int1Neg:
                    return -(int)DeserializeByte(input);
                case Protocol16Type.Int2:
                    return (int)DeserializeUShort(input);
                case Protocol16Type.Int2Neg:
                    return -(int)DeserializeUShort(input);
                case Protocol16Type.Long1:
                    return (long)DeserializeByte(input);
                case Protocol16Type.Long1Neg:
                    return -(long)DeserializeByte(input);
                case Protocol16Type.Long2:
                    return (long)DeserializeUShort(input);
                case Protocol16Type.Long2Neg:
                    return -(long)DeserializeUShort(input);
                case Protocol16Type.Custom:
                    return DeserializeCustom(input, 0);
                case Protocol16Type.Dictionary:
                case Protocol16Type.Hashtable:
                    return DeserializeDictionary(input);
                case Protocol16Type.ObjectArray:
                    return DeserializeObjectArray(input);
                case Protocol16Type.OperationRequest:
                    return DeserializeOperationRequest(input);
                case Protocol16Type.OperationResponse:
                    return DeserializeOperationResponse(input);
                case Protocol16Type.EventData:
                    return DeserializeEventData(input);
                case Protocol16Type.BoolFalse:
                    return false;
                case Protocol16Type.BoolTrue:
                    return true;
                case Protocol16Type.ShortZero:
                    return (short)0;
                case Protocol16Type.IntZero:
                    return 0;
                case Protocol16Type.LongZero:
                    return 0L;
                case Protocol16Type.FloatZero:
                    return 0f;
                case Protocol16Type.DoubleZero:
                    return 0d;
                case Protocol16Type.ByteZero:
                    return (byte)0;
                case Protocol16Type.Array:
                    return DeserializeNestedArray(input);
                default:
                    if ((typeCode & (byte)Protocol16Type.Array) == (byte)Protocol16Type.Array)
                    {
                        return DeserializeTypedArray(input, (byte)(typeCode & ~(byte)Protocol16Type.Array));
                    }

                    // Genuinely unknown type code: we can't know its length, so the caller (a
                    // parameter table/array loop) must stop consuming further entries.
                    return new UnsupportedPhotonValue(typeCode);
            }
        }

        public static OperationRequest DeserializeOperationRequest(Protocol16Stream input)
        {
            byte operationCode = DeserializeByte(input);
            Dictionary<byte, object?> parameters = DeserializeParameterTable(input);

            return new OperationRequest(operationCode, parameters);
        }

        public static OperationResponse DeserializeOperationResponse(Protocol16Stream input)
        {
            byte operationCode = DeserializeByte(input);
            short returnCode = DeserializeShort(input);

            string debugMessage = string.Empty;
            if (Remaining(input) > 0)
            {
                byte debugTypeCode = DeserializeByte(input);
                if (Deserialize(input, debugTypeCode) is string debugString)
                {
                    debugMessage = debugString;
                }
            }

            Dictionary<byte, object?> parameters = DeserializeParameterTable(input);

            return new OperationResponse(operationCode, returnCode, debugMessage, parameters);
        }

        public static EventData DeserializeEventData(Protocol16Stream input)
        {
            byte code = DeserializeByte(input);
            Dictionary<byte, object?> parameters = DeserializeParameterTable(input);

            return new EventData(code, parameters);
        }

        #endregion

        #region private methods

        private static long Remaining(Protocol16Stream input) => input.Length - input.Position;

        private static byte DeserializeByte(Protocol16Stream input) => (byte)input.ReadByte();

        public static short DeserializeShort(Protocol16Stream input)
        {
            Span<byte> buffer = stackalloc byte[2];
            input.Read(buffer);
            return BitConverter.ToInt16(buffer);
        }

        private static ushort DeserializeUShort(Protocol16Stream input)
        {
            Span<byte> buffer = stackalloc byte[2];
            input.Read(buffer);
            return BitConverter.ToUInt16(buffer);
        }

        private static float DeserializeFloat(Protocol16Stream input)
        {
            Span<byte> buffer = stackalloc byte[4];
            input.Read(buffer);
            return BitConverter.ToSingle(buffer);
        }

        private static double DeserializeDouble(Protocol16Stream input)
        {
            Span<byte> buffer = stackalloc byte[8];
            input.Read(buffer);
            return BitConverter.ToDouble(buffer);
        }

        // LEB128 varint, mirroring OpenRadar's readCompressedUint32 (shift cap 35 = ceil(32/7)*7).
        private static uint DeserializeCompressedUInt32(Protocol16Stream input)
        {
            uint value = 0;
            int shift = 0;
            while (true)
            {
                int b = input.ReadByte();
                if (b < 0)
                {
                    return 0;
                }

                value |= (uint)(b & 0x7f) << shift;
                if ((b & 0x80) == 0)
                {
                    return value;
                }

                shift += 7;
                if (shift >= 35)
                {
                    return 0;
                }
            }
        }

        private static ulong DeserializeCompressedUInt64(Protocol16Stream input)
        {
            ulong value = 0;
            int shift = 0;
            while (true)
            {
                int b = input.ReadByte();
                if (b < 0)
                {
                    return 0;
                }

                value |= (ulong)(b & 0x7f) << shift;
                if ((b & 0x80) == 0)
                {
                    return value;
                }

                shift += 7;
                if (shift >= 70)
                {
                    return 0;
                }
            }
        }

        private static int DeserializeCompressedInt32(Protocol16Stream input)
        {
            uint v = DeserializeCompressedUInt32(input);
            return (int)(v >> 1) ^ -(int)(v & 1);
        }

        private static long DeserializeCompressedInt64(Protocol16Stream input)
        {
            ulong v = DeserializeCompressedUInt64(input);
            return (long)(v >> 1) ^ -(long)(v & 1);
        }

        private static int DeserializeCount(Protocol16Stream input)
        {
            int count = (int)DeserializeCompressedUInt32(input);
            if (count < 0 || count > MaxArraySize || count > Remaining(input))
            {
                return 0;
            }

            return count;
        }

        private static string DeserializeString(Protocol16Stream input)
        {
            int length = (int)DeserializeCompressedUInt32(input);
            if (length <= 0 || length > Remaining(input))
            {
                return string.Empty;
            }

            var buffer = new byte[length];
            input.Read(buffer, 0, length);

            return Encoding.UTF8.GetString(buffer);
        }

        // gpType is the original type-code byte: for slim custom types (>= CustomTypeSlimBase)
        // the type-id is folded into the tag itself, so there's no separate id byte to skip;
        // for the legacy Custom(19) marker (called with gpType=0) there is one.
        private static byte[] DeserializeCustom(Protocol16Stream input, byte gpType)
        {
            if (gpType < (byte)Protocol16Type.CustomTypeSlimBase)
            {
                input.ReadByte();
            }

            int size = DeserializeCount(input);
            var data = new byte[size];
            input.Read(data, 0, size);

            return data;
        }

        private static Dictionary<object, object?> DeserializeDictionary(Protocol16Stream input)
        {
            byte keyTypeCode = DeserializeByte(input);
            byte valueTypeCode = DeserializeByte(input);
            int count = DeserializeCount(input);

            var result = new Dictionary<object, object?>(count);
            for (int i = 0; i < count; i++)
            {
                byte kt = keyTypeCode != 0 ? keyTypeCode : DeserializeByte(input);
                byte vt = valueTypeCode != 0 ? valueTypeCode : DeserializeByte(input);
                object? key = Deserialize(input, kt);
                object? value = Deserialize(input, vt);

                if (key is null or UnsupportedPhotonValue || value is UnsupportedPhotonValue)
                {
                    break;
                }

                result[key] = value;
            }

            return result;
        }

        private static object?[] DeserializeObjectArray(Protocol16Stream input)
        {
            int size = DeserializeCount(input);
            var array = new object?[size];
            for (int i = 0; i < size; i++)
            {
                byte typeCode = DeserializeByte(input);
                array[i] = Deserialize(input, typeCode);
            }

            return array;
        }

        // typeCode == Array (0x40) exactly: the element type isn't folded into the tag, so it's
        // read as a separate byte before the elements (unlike DeserializeTypedArray below).
        private static object?[] DeserializeNestedArray(Protocol16Stream input)
        {
            int size = DeserializeCount(input);
            byte elementTypeCode = DeserializeByte(input);
            var array = new object?[size];
            for (int i = 0; i < size; i++)
            {
                array[i] = Deserialize(input, elementTypeCode);
            }

            return array;
        }

        private static object DeserializeTypedArray(Protocol16Stream input, byte elementTypeCode)
        {
            int size = DeserializeCount(input);

            switch ((Protocol16Type)elementTypeCode)
            {
                case Protocol16Type.Boolean:
                    {
                        var result = new bool[size];
                        int packedByteCount = (size + 7) / 8;
                        var packed = new byte[packedByteCount];
                        input.Read(packed, 0, packedByteCount);
                        for (int i = 0; i < size; i++)
                        {
                            result[i] = (packed[i / 8] & (1 << (i % 8))) != 0;
                        }

                        return result;
                    }
                case Protocol16Type.Byte:
                    {
                        var data = new byte[size];
                        input.Read(data, 0, size);
                        return data;
                    }
                case Protocol16Type.Short:
                    {
                        var result = new short[size];
                        for (int i = 0; i < size; i++)
                        {
                            result[i] = DeserializeShort(input);
                        }

                        return result;
                    }
                case Protocol16Type.Float:
                    {
                        var result = new float[size];
                        for (int i = 0; i < size; i++)
                        {
                            result[i] = DeserializeFloat(input);
                        }

                        return result;
                    }
                case Protocol16Type.Double:
                    {
                        var result = new double[size];
                        for (int i = 0; i < size; i++)
                        {
                            result[i] = DeserializeDouble(input);
                        }

                        return result;
                    }
                case Protocol16Type.String:
                    {
                        var result = new string[size];
                        for (int i = 0; i < size; i++)
                        {
                            result[i] = DeserializeString(input);
                        }

                        return result;
                    }
                case Protocol16Type.CompressedInt:
                    {
                        var result = new int[size];
                        for (int i = 0; i < size; i++)
                        {
                            result[i] = DeserializeCompressedInt32(input);
                        }

                        return result;
                    }
                case Protocol16Type.CompressedLong:
                    {
                        var result = new long[size];
                        for (int i = 0; i < size; i++)
                        {
                            result[i] = DeserializeCompressedInt64(input);
                        }

                        return result;
                    }
                case Protocol16Type.Dictionary:
                case Protocol16Type.Hashtable:
                    {
                        var result = new object?[size];
                        for (int i = 0; i < size; i++)
                        {
                            result[i] = DeserializeDictionary(input);
                        }

                        return result;
                    }
                case Protocol16Type.Custom:
                    {
                        // Elements share one customTypeId, read once up front.
                        input.ReadByte();
                        var result = new byte[size][];
                        for (int i = 0; i < size; i++)
                        {
                            int elementSize = DeserializeCount(input);
                            var data = new byte[elementSize];
                            input.Read(data, 0, elementSize);
                            result[i] = data;
                        }

                        return result;
                    }
                default:
                    {
                        var result = new object?[size];
                        for (int i = 0; i < size; i++)
                        {
                            result[i] = Deserialize(input, elementTypeCode);
                        }

                        return result;
                    }
            }
        }

        private static Dictionary<byte, object?> DeserializeParameterTable(Protocol16Stream input)
        {
            int count = DeserializeCount(input);
            var dictionary = new Dictionary<byte, object?>(count);
            for (int i = 0; i < count; i++)
            {
                if (Remaining(input) <= 0)
                {
                    break;
                }

                byte key = DeserializeByte(input);
                byte valueTypeCode = DeserializeByte(input);
                object? value = Deserialize(input, valueTypeCode);
                dictionary[key] = value;

                if (value is UnsupportedPhotonValue)
                {
                    // A genuinely unrecognized type: its length is unknown, so further bytes in
                    // this table can't be reliably interpreted as key/type/value triples.
                    break;
                }
            }

            return dictionary;
        }

        #endregion
    }
}
