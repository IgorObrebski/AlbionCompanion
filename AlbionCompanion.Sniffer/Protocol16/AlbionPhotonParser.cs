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
            //
            // Stash payload length/prefix on the exception (rather than changing OnParseFailure's
            // signature) so subscribers logging this can correlate it against debug_packets.log
            // without every consumer needing to plumb the raw payload through.
            ex.Data["PayloadLength"] = payload.Length;
            ex.Data["PayloadHexPrefix"] = Convert.ToHexString(payload, 0, Math.Min(payload.Length, 32));
            OnParseFailure?.Invoke(this, ex);
        }
    }

    protected override void OnEvent(byte code, Dictionary<byte, object?> parameters) =>
        RaiseIsolated(OnEventReceived, new PhotonEvent(code, parameters));

    protected override void OnRequest(byte operationCode, Dictionary<byte, object?> parameters) =>
        RaiseIsolated(OnRequestReceived, new PhotonRequest(operationCode, parameters));

    protected override void OnResponse(byte operationCode, short returnCode, string debugMessage, Dictionary<byte, object?> parameters) =>
        RaiseIsolated(OnResponseReceived, new PhotonResponse(operationCode, returnCode, debugMessage, parameters));

    // These overrides run synchronously inside PhotonParser.ReceivePacket's per-command loop
    // (see HandlePayload's NOTE). A subscriber that throws here - confirmed via live capture:
    // Convert.ToByte overflowing on an unexpectedly large parameter value - would otherwise
    // propagate out of ReceivePacket and abort every remaining command in the same UDP datagram,
    // silently dropping unrelated data that had nothing to do with the failing subscriber.
    //
    // Isolated PER SUBSCRIBER, not per overall dispatch: a plain `handler?.Invoke(...)` on a
    // multicast delegate with multiple independent subscribers (e.g. AlbionEventLogger,
    // LocalPlayerTracker, ZoneTracker all listen to OnResponseReceived) stops at the first
    // throwing subscriber - every subscriber registered AFTER it in the invocation list silently
    // never runs for that one event, with nothing in any log identifying which subscriber failed
    // or which later ones got skipped. Confirmed as a live, real risk on 2026-07-17 investigating
    // a session where zone changes intermittently went unrecognized with no error trail. Invoking
    // each subscriber in its own try/catch means one broken handler can never silently starve
    // another.
    private void RaiseIsolated<TEventArgs>(EventHandler<TEventArgs>? handler, TEventArgs args)
    {
        if (handler is null)
        {
            return;
        }

        foreach (var subscriber in handler.GetInvocationList())
        {
            try
            {
                ((EventHandler<TEventArgs>)subscriber).Invoke(this, args);
            }
            catch (Exception ex)
            {
                OnParseFailure?.Invoke(this, ex);
            }
        }
    }
}
