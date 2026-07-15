namespace AlbionCompanion.Sniffer.PacketCapture;

public interface IPacketSniffer
{
    void Start();
    void Stop();
    event EventHandler<byte[]> OnPhotonPayloadReceived;
}
