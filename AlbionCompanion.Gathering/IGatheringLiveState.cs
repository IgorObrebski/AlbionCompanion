namespace AlbionCompanion.Gathering;

public interface IGatheringLiveState
{
    bool IsActive { get; }
    string? StartLocation { get; }
    string? CurrentLocation { get; }
    int TotalFame { get; }
    int TotalSilver { get; }
    IReadOnlyList<ItemLocationTotal> ItemTotals { get; }
    IReadOnlyList<LocationTotal> FameByLocation { get; }
    IReadOnlyList<LocationTotal> SilverByLocation { get; }

    event EventHandler? OnChanged;

    Task Attach(IGatheringSessionService sessionService);
}
