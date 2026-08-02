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

    private static PhotonEvent NewHarvestableObject(int nodeId, int tier, int? enchantmentLevel = null)
    {
        var parameters = new Dictionary<byte, object?> { [0] = nodeId, [5] = 27, [7] = tier, [252] = (byte)40 };
        if (enchantmentLevel is { } level)
        {
            parameters[11] = level;
        }

        return new PhotonEvent(1, parameters);
    }

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
        Assert.Null(tracker.GetEnchantmentLevel(999999));
        Assert.Null(tracker.GetCategoryCode(999999));
    }

    [Fact]
    public void NewHarvestableObjectEvent_RecordsCategoryCodeForNodeId()
    {
        // Needed because HarvestFinished (unlike HarvestStart) carries no category code at all -
        // GatheringEventRouter.ResolveItemId resolves it entirely through this tracker.
        var parser = new FakePhotonParser();
        var tracker = new HarvestableNodeTracker(parser);

        parser.RaiseEvent(NewHarvestableObject(nodeId: 2951, tier: 4));

        Assert.Equal(27, tracker.GetCategoryCode(2951));
    }

    [Fact]
    public void OtherSemanticEventCode_IsIgnored()
    {
        var parser = new FakePhotonParser();
        var tracker = new HarvestableNodeTracker(parser);

        parser.RaiseEvent(new PhotonEvent(1, new Dictionary<byte, object?> { [0] = 2951, [252] = (byte)59 }));

        Assert.Null(tracker.GetTier(2951));
    }

    [Fact]
    public void NewHarvestableObjectEvent_RecordsEnchantmentLevelForNodeId()
    {
        var parser = new FakePhotonParser();
        var tracker = new HarvestableNodeTracker(parser);

        parser.RaiseEvent(NewHarvestableObject(nodeId: 2462, tier: 5, enchantmentLevel: 2));

        Assert.Equal(2, tracker.GetEnchantmentLevel(2462));
    }

    [Fact]
    public void NewHarvestableObjectEvent_MissingEnchantmentParameter_DefaultsToZero()
    {
        var parser = new FakePhotonParser();
        var tracker = new HarvestableNodeTracker(parser);

        parser.RaiseEvent(NewHarvestableObject(nodeId: 2972, tier: 4));

        Assert.Equal(0, tracker.GetEnchantmentLevel(2972));
    }
}
