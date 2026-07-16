using AlbionCompanion.Sniffer.Protocol16;
using Xunit;

namespace AlbionCompanion.Gathering.Tests;

public class HarvestableNodeTrackerTests
{
    private sealed class FakePhotonParser : IPhotonParser
    {
        public event EventHandler<PhotonEvent>? OnEventReceived;
        public event EventHandler<PhotonResponse>? OnResponseReceived;
        public event EventHandler<PhotonRequest>? OnRequestReceived;
        public void HandlePayload(byte[] payload) { }
        public void RaiseEvent(PhotonEvent photonEvent) => OnEventReceived?.Invoke(this, photonEvent);
    }

    private static PhotonEvent NewHarvestableObject(int nodeId, int tier) =>
        new(1, new Dictionary<byte, object?> { [0] = nodeId, [5] = 27, [7] = tier, [252] = (byte)40 });

    [Fact]
    public void NewHarvestableObjectEvent_RecordsTierForNodeId()
    {
        var parser = new FakePhotonParser();
        var tracker = new HarvestableNodeTracker(parser);

        parser.RaiseEvent(NewHarvestableObject(nodeId: 2951, tier: 4));

        Assert.Equal(4, tracker.GetTier(2951));
    }

    [Fact]
    public void UnknownNodeId_ReturnsNull()
    {
        var parser = new FakePhotonParser();
        var tracker = new HarvestableNodeTracker(parser);

        Assert.Null(tracker.GetTier(999999));
    }

    [Fact]
    public void OtherSemanticEventCode_IsIgnored()
    {
        var parser = new FakePhotonParser();
        var tracker = new HarvestableNodeTracker(parser);

        parser.RaiseEvent(new PhotonEvent(1, new Dictionary<byte, object?> { [0] = 2951, [252] = (byte)59 }));

        Assert.Null(tracker.GetTier(2951));
    }
}
