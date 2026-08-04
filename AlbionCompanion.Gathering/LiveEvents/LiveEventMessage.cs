using System.Text.Json;
using System.Text.Json.Serialization;

namespace AlbionCompanion.Gathering.LiveEvents;

// One line of newline-delimited JSON per message, sent both directions over the same named pipe:
// Service -> App carries the six gathering-session events IGatheringSessionService already
// raises in-process; App -> Service carries CharacterRegistryChanged (the one thing the App still
// writes directly to the database, so the Service's LocalPlayerTracker cache needs telling).
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(SessionStartedMessage), "SessionStarted")]
[JsonDerivedType(typeof(SessionEndedMessage), "SessionEnded")]
[JsonDerivedType(typeof(LocationChangedMessage), "LocationChanged")]
[JsonDerivedType(typeof(ItemAddedMessage), "ItemAdded")]
[JsonDerivedType(typeof(FameAddedMessage), "FameAdded")]
[JsonDerivedType(typeof(SilverAddedMessage), "SilverAdded")]
[JsonDerivedType(typeof(CharacterRegistryChangedMessage), "CharacterRegistryChanged")]
public abstract record LiveEventMessage;

public sealed record SessionStartedMessage(Guid SessionId, string StartLocation, Guid? CharacterId) : LiveEventMessage;
public sealed record SessionEndedMessage(Guid SessionId) : LiveEventMessage;
public sealed record LocationChangedMessage(Guid SessionId, string CurrentLocation) : LiveEventMessage;
public sealed record ItemAddedMessage(string ItemId, int Amount, string Location) : LiveEventMessage;
public sealed record FameAddedMessage(int Amount, string Location) : LiveEventMessage;
public sealed record SilverAddedMessage(int Amount, string Location) : LiveEventMessage;
public sealed record CharacterRegistryChangedMessage : LiveEventMessage;

public static class LiveEventMessageSerializer
{
    public static string Serialize(LiveEventMessage message) =>
        JsonSerializer.Serialize(message, typeof(LiveEventMessage));

    public static LiveEventMessage Deserialize(string line) =>
        (LiveEventMessage)JsonSerializer.Deserialize(line, typeof(LiveEventMessage))!;
}
