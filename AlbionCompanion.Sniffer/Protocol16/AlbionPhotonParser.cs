using PhotonPackageParser;

namespace AlbionCompanion.Sniffer.Protocol16;

public class AlbionPhotonParser : PhotonParser, IPhotonParser
{
    public event EventHandler<PhotonEvent>? OnEventReceived;
    public event EventHandler<PhotonResponse>? OnResponseReceived;

    public void HandlePayload(byte[] payload) => ReceivePacket(payload);

    protected override void OnEvent(byte code, Dictionary<byte, object> parameters) =>
        OnEventReceived?.Invoke(this, new PhotonEvent(code, parameters));

    protected override void OnRequest(byte operationCode, Dictionary<byte, object> parameters)
    {
        // Requests aren't part of the MVP discovery-mode logging (spec only defines EVENT/RESPONSE log lines).
    }

    protected override void OnResponse(byte operationCode, short returnCode, string debugMessage, Dictionary<byte, object> parameters) =>
        OnResponseReceived?.Invoke(this, new PhotonResponse(operationCode, returnCode, debugMessage, parameters));
}
