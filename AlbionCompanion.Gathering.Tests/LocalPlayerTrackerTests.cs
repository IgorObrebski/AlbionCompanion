using AlbionCompanion.Sniffer.Protocol16;
using Xunit;

namespace AlbionCompanion.Gathering.Tests;

public class LocalPlayerTrackerTests
{
    private sealed class FakePhotonParser : IPhotonParser
    {
        public event EventHandler<PhotonEvent>? OnEventReceived;
        public event EventHandler<PhotonResponse>? OnResponseReceived;
        public event EventHandler<PhotonRequest>? OnRequestReceived;
        public void HandlePayload(byte[] payload) { }
        public void RaiseResponse(PhotonResponse response) => OnResponseReceived?.Invoke(this, response);
    }

    private static PhotonResponse ZoneJoinResponse(int ownEntityId) =>
        new(1, 0, string.Empty, new Dictionary<byte, object?> { [0] = ownEntityId, [253] = 2 });

    [Fact]
    public void ZoneJoinResponse_RecordsOwnEntityId()
    {
        var parser = new FakePhotonParser();
        var tracker = new LocalPlayerTracker(parser);

        parser.RaiseResponse(ZoneJoinResponse(200760));

        Assert.Equal(200760, tracker.CurrentEntityId);
    }

    [Fact]
    public void SubsequentZoneJoin_UpdatesToTheNewEntityId()
    {
        // Regression: entity ids are reassigned per zone, not stable across zone changes -
        // confirmed via live capture where the same character got a different id on each join.
        var parser = new FakePhotonParser();
        var tracker = new LocalPlayerTracker(parser);

        parser.RaiseResponse(ZoneJoinResponse(200760));
        parser.RaiseResponse(ZoneJoinResponse(1111937));

        Assert.Equal(1111937, tracker.CurrentEntityId);
    }

    [Fact]
    public void ResponseWithoutZoneJoinSubCode_IsIgnored()
    {
        var parser = new FakePhotonParser();
        var tracker = new LocalPlayerTracker(parser);

        parser.RaiseResponse(new PhotonResponse(1, 0, string.Empty,
            new Dictionary<byte, object?> { [0] = 999, [253] = 52 }));

        Assert.Null(tracker.CurrentEntityId);
    }
}
