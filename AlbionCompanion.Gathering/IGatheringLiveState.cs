using AlbionCompanion.Core.Models;

namespace AlbionCompanion.Gathering;

public interface IGatheringLiveState
{
    bool IsActive { get; }
    string? StartLocation { get; }
    string? CurrentLocation { get; }
    Guid? CharacterId { get; }
    int TotalFame { get; }
    int TotalSilver { get; }
    IReadOnlyList<ItemLocationTotal> ItemTotals { get; }
    IReadOnlyList<LocationTotal> FameByLocation { get; }
    IReadOnlyList<LocationTotal> SilverByLocation { get; }

    event EventHandler? OnChanged;
    // Passthrough of IGatheringSessionService.OnSessionStarted's own event, re-raised from this
    // already-DI-safe Blazor singleton - lets UI components (the session-start toast) subscribe
    // without injecting IGatheringSessionService directly, which lives in a scope that isn't
    // guaranteed ready by the time Blazor components start resolving DI (see App.xaml.cs's
    // fire-and-forget StartGatheringAsync).
    event EventHandler<GatheringSession>? OnSessionStarted;

    Task Attach(IGatheringSessionService sessionService, LiveEvents.IGatheringLiveEventSource eventSource);
}
