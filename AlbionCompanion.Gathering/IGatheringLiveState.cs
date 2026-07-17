namespace AlbionCompanion.Gathering;

public interface IGatheringLiveState
{
    bool IsActive { get; }
    string? StartLocation { get; }
    int TotalFame { get; }
    IReadOnlyDictionary<string, int> ItemTotals { get; }

    event EventHandler? OnChanged;

    void Attach(IGatheringSessionService sessionService);
}
