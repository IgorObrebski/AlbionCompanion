namespace AlbionCompanion.Gathering;

public interface IGatheringLiveState
{
    bool IsActive { get; }
    string? StartLocation { get; }
    int TotalFame { get; }
    IReadOnlyList<ItemLocationTotal> ItemTotals { get; }

    event EventHandler? OnChanged;

    Task Attach(IGatheringSessionService sessionService);
}
