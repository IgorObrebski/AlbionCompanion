using PhotonPackageParser;

namespace AlbionCompanion.Sniffer.Protocol16;

public class AlbionPhotonParser : PhotonParser, IPhotonParser
{
    public event EventHandler<PhotonEvent>? OnEventReceived;
    public event EventHandler<PhotonResponse>? OnResponseReceived;

    public void HandlePayload(byte[] payload)
    {
        try
        {
            ReceivePacket(payload);
        }
        catch (Exception)
        {
            // PhotonPackageParser doesn't implement every Photon parameter type code Albion sends
            // (e.g. type code 153), and malformed/truncated captures can also throw mid-parse.
            // One bad payload must not stop the sniffer from processing the rest of the traffic.
        }
    }

    protected override void OnEvent(byte code, Dictionary<byte, object> parameters) =>
        OnEventReceived?.Invoke(this, new PhotonEvent(code, parameters));

    protected override void OnRequest(byte operationCode, Dictionary<byte, object> parameters)
    {
        // Requests aren't part of the MVP discovery-mode logging (spec only defines EVENT/RESPONSE log lines).
    }

    protected override void OnResponse(byte operationCode, short returnCode, string debugMessage, Dictionary<byte, object> parameters) =>
        OnResponseReceived?.Invoke(this, new PhotonResponse(operationCode, returnCode, debugMessage, parameters));
}
