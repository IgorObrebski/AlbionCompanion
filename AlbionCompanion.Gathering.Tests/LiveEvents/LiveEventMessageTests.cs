using AlbionCompanion.Gathering.LiveEvents;
using Xunit;

namespace AlbionCompanion.Gathering.Tests.LiveEvents;

public class LiveEventMessageTests
{
    [Fact]
    public void SessionStartedMessage_RoundTripsThroughSerializer()
    {
        var original = new SessionStartedMessage(Guid.NewGuid(), "Martlock", Guid.NewGuid());

        var line = LiveEventMessageSerializer.Serialize(original);
        var result = LiveEventMessageSerializer.Deserialize(line);

        var deserialized = Assert.IsType<SessionStartedMessage>(result);
        Assert.Equal(original.SessionId, deserialized.SessionId);
        Assert.Equal(original.StartLocation, deserialized.StartLocation);
        Assert.Equal(original.CharacterId, deserialized.CharacterId);
    }

    [Fact]
    public void SessionEndedMessage_RoundTripsThroughSerializer()
    {
        var sessionId = Guid.NewGuid();
        var original = new SessionEndedMessage(sessionId);

        var line = LiveEventMessageSerializer.Serialize(original);
        var result = LiveEventMessageSerializer.Deserialize(line);

        var deserialized = Assert.IsType<SessionEndedMessage>(result);
        Assert.Equal(sessionId, deserialized.SessionId);
    }

    [Fact]
    public void LocationChangedMessage_RoundTripsThroughSerializer()
    {
        var sessionId = Guid.NewGuid();
        var original = new LocationChangedMessage(sessionId, "Caerleon");

        var line = LiveEventMessageSerializer.Serialize(original);
        var result = LiveEventMessageSerializer.Deserialize(line);

        var deserialized = Assert.IsType<LocationChangedMessage>(result);
        Assert.Equal(sessionId, deserialized.SessionId);
        Assert.Equal("Caerleon", deserialized.CurrentLocation);
    }

    [Fact]
    public void ItemAddedMessage_RoundTripsThroughSerializer()
    {
        var original = new ItemAddedMessage("T4_ORE", 5, "Martlock");

        var line = LiveEventMessageSerializer.Serialize(original);
        var result = LiveEventMessageSerializer.Deserialize(line);

        var deserialized = Assert.IsType<ItemAddedMessage>(result);
        Assert.Equal("T4_ORE", deserialized.ItemId);
        Assert.Equal(5, deserialized.Amount);
        Assert.Equal("Martlock", deserialized.Location);
    }

    [Fact]
    public void FameAddedMessage_RoundTripsThroughSerializer()
    {
        var original = new FameAddedMessage(500, "Martlock");

        var line = LiveEventMessageSerializer.Serialize(original);
        var result = LiveEventMessageSerializer.Deserialize(line);

        var deserialized = Assert.IsType<FameAddedMessage>(result);
        Assert.Equal(500, deserialized.Amount);
        Assert.Equal("Martlock", deserialized.Location);
    }

    [Fact]
    public void SilverAddedMessage_RoundTripsThroughSerializer()
    {
        var original = new SilverAddedMessage(2500, "Caerleon");

        var line = LiveEventMessageSerializer.Serialize(original);
        var result = LiveEventMessageSerializer.Deserialize(line);

        var deserialized = Assert.IsType<SilverAddedMessage>(result);
        Assert.Equal(2500, deserialized.Amount);
        Assert.Equal("Caerleon", deserialized.Location);
    }

    [Fact]
    public void CharacterRegistryChangedMessage_RoundTripsThroughSerializer()
    {
        var original = new CharacterRegistryChangedMessage();

        var line = LiveEventMessageSerializer.Serialize(original);
        var result = LiveEventMessageSerializer.Deserialize(line);

        Assert.IsType<CharacterRegistryChangedMessage>(result);
    }
}
