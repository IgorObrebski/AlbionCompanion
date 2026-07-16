using PhotonPackageParser;

namespace AlbionCompanion.Sniffer.Protocol16;

public class AlbionPhotonParser : PhotonParser, IPhotonParser
{
    public event EventHandler<PhotonEvent>? OnEventReceived;
    public event EventHandler<PhotonResponse>? OnResponseReceived;
    public event EventHandler<PhotonRequest>? OnRequestReceived;
    public event EventHandler<Exception>? OnParseFailure;

    public void HandlePayload(byte[] payload)
    {
        try
        {
            ReceivePacket(payload);
        }
        catch (Exception ex)
        {
            // PhotonPackageParser doesn't implement every Photon parameter type code Albion sends
            // (e.g. type code 153), and malformed/truncated captures can also throw mid-parse.
            // One bad payload must not stop the sniffer from processing the rest of the traffic.
            // NOTE: a packet can bundle multiple Photon commands (see PhotonParser.ReceivePacket's
            // command loop) - an exception here aborts every remaining command in this datagram.
            OnParseFailure?.Invoke(this, ex);
        }
    }

    protected override void OnEvent(byte code, Dictionary<byte, object?> parameters) =>
        RaiseIsolated(() => OnEventReceived?.Invoke(this, new PhotonEvent(code, parameters)));

    protected override void OnRequest(byte operationCode, Dictionary<byte, object?> parameters) =>
        RaiseIsolated(() => OnRequestReceived?.Invoke(this, new PhotonRequest(operationCode, parameters)));

    protected override void OnResponse(byte operationCode, short returnCode, string debugMessage, Dictionary<byte, object?> parameters) =>
        RaiseIsolated(() => OnResponseReceived?.Invoke(this, new PhotonResponse(operationCode, returnCode, debugMessage, parameters)));

    // These overrides run synchronously inside PhotonParser.ReceivePacket's per-command loop
    // (see HandlePayload's NOTE). A subscriber that throws here - confirmed via live capture:
    // Convert.ToByte overflowing on an unexpectedly large parameter value - would otherwise
    // propagate out of ReceivePacket and abort every remaining command in the same UDP datagram,
    // silently dropping unrelated data that had nothing to do with the failing subscriber.
    private void RaiseIsolated(Action raiseEvent)
    {
        try
        {
            raiseEvent();
        }
        catch (Exception ex)
        {
            OnParseFailure?.Invoke(this, ex);
        }
    }
}
