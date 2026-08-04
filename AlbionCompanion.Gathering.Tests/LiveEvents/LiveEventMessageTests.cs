using AlbionCompanion.Core.Models;
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
    public void CharacterRegistryChangedMessage_RoundTripsThroughSerializer()
    {
        var original = new CharacterRegistryChangedMessage();

        var line = LiveEventMessageSerializer.Serialize(original);
        var result = LiveEventMessageSerializer.Deserialize(line);

        Assert.IsType<CharacterRegistryChangedMessage>(result);
    }
}
