namespace AlbionCompanion.Sniffer.Protocol16;

public interface IPhotonParser
{
    void HandlePayload(byte[] payload);
    event EventHandler<PhotonEvent> OnEventReceived;
    event EventHandler<PhotonResponse> OnResponseReceived;
    event EventHandler<PhotonRequest> OnRequestReceived;
}
