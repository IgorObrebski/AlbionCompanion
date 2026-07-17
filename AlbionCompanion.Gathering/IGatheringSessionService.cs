using AlbionCompanion.Core.Models;

namespace AlbionCompanion.Gathering;

public interface IGatheringSessionService
{
    Task StartSessionAsync(string location);
    Task EndSessionAsync();
    Task AddItemAsync(string itemId, int amount);
    Task AddFameAsync(string fameType, int amount);
    Task<GatheringSession?> GetActiveSessionAsync();

    event EventHandler<GatheringSession>? OnSessionStarted;
    event EventHandler<GatheringSession>? OnSessionEnded;
    event EventHandler<GatheredItem>? OnItemAdded;
    event EventHandler<FameLog>? OnFameAdded;
}
