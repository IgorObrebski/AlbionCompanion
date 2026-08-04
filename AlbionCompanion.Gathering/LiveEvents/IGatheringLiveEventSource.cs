using AlbionCompanion.Core.Models;

namespace AlbionCompanion.Gathering.LiveEvents;

// Common event shape shared by IGatheringSessionService (the real, in-process source - used
// directly inside AlbionCompanion.Service) and LiveEventPipeClient (the App's piped source).
// GatheringLiveState subscribes to whichever one it's given without caring which.
public interface IGatheringLiveEventSource
{
    event EventHandler<GatheringSession>? OnSessionStarted;
    event EventHandler<GatheringSession>? OnSessionEnded;
    event EventHandler<GatheringSession>? OnLocationChanged;
    event EventHandler<GatheredItem>? OnItemAdded;
    event EventHandler<FameLog>? OnFameAdded;
    event EventHandler<SilverLog>? OnSilverAdded;
}
