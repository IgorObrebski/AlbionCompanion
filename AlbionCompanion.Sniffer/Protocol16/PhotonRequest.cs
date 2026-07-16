namespace AlbionCompanion.Sniffer.Protocol16;

public sealed record PhotonRequest(byte OperationCode, Dictionary<byte, object?> Parameters);
