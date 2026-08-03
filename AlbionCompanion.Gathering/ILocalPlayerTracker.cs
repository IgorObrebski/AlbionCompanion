namespace AlbionCompanion.Gathering;

public interface ILocalPlayerTracker
{
    int? CurrentEntityId { get; }
    string? CurrentCharacterName { get; }

    event EventHandler<Exception>? OnError;
}
