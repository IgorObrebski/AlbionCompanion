namespace AlbionCompanion.Sniffer.Protocol16;

public sealed record PhotonEvent(byte Code, Dictionary<byte, object> Parameters);
