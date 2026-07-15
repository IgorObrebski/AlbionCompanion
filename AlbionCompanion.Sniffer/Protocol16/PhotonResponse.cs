namespace AlbionCompanion.Sniffer.Protocol16;

public sealed record PhotonResponse(byte OperationCode, short ReturnCode, string DebugMessage, Dictionary<byte, object> Parameters);
