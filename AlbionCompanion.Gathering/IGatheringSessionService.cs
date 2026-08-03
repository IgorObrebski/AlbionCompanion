using AlbionCompanion.Core.Models;

namespace AlbionCompanion.Gathering;

public interface IGatheringSessionService
{
    Task StartSessionAsync(string location);
    Task EndSessionAsync();
    Task AddItemAsync(string itemId, int amount);
    Task AddFameAsync(string fameType, int amount);
    Task AddSilverAsync(int amount);
    Task<GatheringSession?> GetActiveSessionAsync();
    Task<ActiveSessionSnapshot?> GetActiveSessionSnapshotAsync();

    event EventHandler<GatheringSession>? OnSessionStarted;
    event EventHandler<GatheringSession>? OnSessionEnded;
    event EventHandler<GatheringSession>? OnLocationChanged;
    event EventHandler<GatheredItem>? OnItemAdded;
    event EventHandler<FameLog>? OnFameAdded;
    event EventHandler<SilverLog>? OnSilverAdded;
}

// Everything IGatheringLiveState needs to rehydrate on startup when a session is already active
// (e.g. the app was closed and relaunched while still standing in open world - the session row
// survived in the DB, per StartSessionAsync's roaming behavior, but nothing re-fires
// OnSessionStarted for a session that already existed before this process started).
public record ActiveSessionSnapshot(
    string CurrentLocation,
    Guid? CharacterId,
    int TotalFameEarned,
    int TotalSilverEarned,
    IReadOnlyList<ItemLocationTotal> ItemTotals,
    IReadOnlyList<LocationTotal> FameByLocation,
    IReadOnlyList<LocationTotal> SilverByLocation);

// One (item, location) bucket's summed amount within a session - a session can roam through
// multiple zones without ending (see the 2026-08-02 roaming fix), so "what did I gather" needs a
// location dimension alongside the item id, not just a flat per-item total.
public record ItemLocationTotal(string ItemId, string Location, int Amount);

// One location's summed amount within a session - used for fame (and any other per-location-only
// total that isn't further broken down by a second key like item id).
public record LocationTotal(string Location, int Amount);
