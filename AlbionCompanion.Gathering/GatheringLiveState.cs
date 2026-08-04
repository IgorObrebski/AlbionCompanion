using AlbionCompanion.Core.Models;
using AlbionCompanion.Gathering.LiveEvents;

namespace AlbionCompanion.Gathering;

public class GatheringLiveState : IGatheringLiveState
{
    // Mutated on the packet-capture/parse thread (via GatheringEventRouter's fire-and-forget event
    // dispatch), read and enumerated on the Blazor UI thread (Broadcast.razor's render). _lock protects
    // every access to the mutable fields below; ItemTotals hands out an immutable snapshot copy
    // rather than the live dictionary, so a UI-thread enumeration can never race a capture-thread
    // mutation (Dictionary<> itself does not support concurrent read/write).
    private readonly object _lock = new();
    private readonly Dictionary<(string ItemId, string Location), int> _itemTotals = new();
    private readonly Dictionary<string, int> _fameByLocation = new();
    private readonly Dictionary<string, int> _silverByLocation = new();

    private bool _isActive;
    private string? _startLocation;
    private string? _currentLocation;
    private Guid? _characterId;
    private int _totalFame;
    private int _totalSilver;

    public bool IsActive
    {
        get { lock (_lock) { return _isActive; } }
    }

    public string? StartLocation
    {
        get { lock (_lock) { return _startLocation; } }
    }

    public string? CurrentLocation
    {
        get { lock (_lock) { return _currentLocation; } }
    }

    public Guid? CharacterId
    {
        get { lock (_lock) { return _characterId; } }
    }

    public int TotalFame
    {
        get { lock (_lock) { return _totalFame; } }
    }

    public int TotalSilver
    {
        get { lock (_lock) { return _totalSilver; } }
    }

    public IReadOnlyList<ItemLocationTotal> ItemTotals
    {
        get
        {
            lock (_lock)
            {
                return _itemTotals.Select(kv => new ItemLocationTotal(kv.Key.ItemId, kv.Key.Location, kv.Value)).ToList();
            }
        }
    }

    public IReadOnlyList<LocationTotal> FameByLocation
    {
        get
        {
            lock (_lock)
            {
                return _fameByLocation.Select(kv => new LocationTotal(kv.Key, kv.Value)).ToList();
            }
        }
    }

    public IReadOnlyList<LocationTotal> SilverByLocation
    {
        get
        {
            lock (_lock)
            {
                return _silverByLocation.Select(kv => new LocationTotal(kv.Key, kv.Value)).ToList();
            }
        }
    }

    public event EventHandler? OnChanged;
    public event EventHandler<GatheringSession>? OnSessionStarted;

    public async Task Attach(IGatheringSessionService sessionService, IGatheringLiveEventSource eventSource)
    {
        // Rehydrate from the DB first: if the app was closed and relaunched while a session was
        // still open (e.g. the player stayed in open world across the restart), that session's row
        // survived (StartSessionAsync's roaming behavior never ends it), but OnSessionStarted only
        // fires for a session created *during this process's lifetime* - without this, the UI would
        // wrongly show "No session" until the player's next gather/zone-change action, even though
        // one has been running the whole time. Safe to do before wiring the event handlers below:
        // any event that arrives after this snapshot can only describe genuinely new activity (a
        // domain event only fires once, at the moment its action happens - there's no replay), so
        // there's no risk of double-counting an item or fame entry the snapshot already included.
        if (await sessionService.GetActiveSessionSnapshotAsync() is { } snapshot)
        {
            lock (_lock)
            {
                _isActive = true;
                _startLocation = snapshot.CurrentLocation;
                _currentLocation = snapshot.CurrentLocation;
                _characterId = snapshot.CharacterId;
                _totalFame = snapshot.TotalFameEarned;
                _totalSilver = snapshot.TotalSilverEarned;
                foreach (var total in snapshot.ItemTotals)
                {
                    _itemTotals[(total.ItemId, total.Location)] = total.Amount;
                }
                foreach (var total in snapshot.FameByLocation)
                {
                    _fameByLocation[total.Location] = total.Amount;
                }
                foreach (var total in snapshot.SilverByLocation)
                {
                    _silverByLocation[total.Location] = total.Amount;
                }
            }

            OnChanged?.Invoke(this, EventArgs.Empty);
        }

        eventSource.OnSessionStarted += (_, session) => Safely(() =>
        {
            lock (_lock)
            {
                _itemTotals.Clear();
                _fameByLocation.Clear();
                _silverByLocation.Clear();
                _totalFame = 0;
                _totalSilver = 0;
                _startLocation = session.StartLocation;
                _currentLocation = session.StartLocation;
                _characterId = session.CharacterId;
                _isActive = true;
            }
        });

        eventSource.OnSessionStarted += (_, session) =>
        {
            try
            {
                OnSessionStarted?.Invoke(this, session);
            }
            catch
            {
                // Same boundary rule as Safely() below - a failing UI subscriber must never
                // destabilize the gathering pipeline this event also drives.
            }
        };

        eventSource.OnSessionEnded += (_, _) => Safely(() =>
        {
            lock (_lock)
            {
                _isActive = false;
            }
        });

        eventSource.OnLocationChanged += (_, session) => Safely(() =>
        {
            lock (_lock)
            {
                _currentLocation = session.CurrentLocation;
            }
        });

        eventSource.OnItemAdded += (_, item) => Safely(() =>
        {
            var key = (item.ItemId, item.Location);
            var amount = item.Amount;
            lock (_lock)
            {
                _itemTotals[key] = _itemTotals.GetValueOrDefault(key) + amount;
            }
        });

        eventSource.OnFameAdded += (_, fameLog) => Safely(() =>
        {
            lock (_lock)
            {
                _totalFame += fameLog.Amount;
                _fameByLocation[fameLog.Location] = _fameByLocation.GetValueOrDefault(fameLog.Location) + fameLog.Amount;
            }
        });

        eventSource.OnSilverAdded += (_, silverLog) => Safely(() =>
        {
            lock (_lock)
            {
                _totalSilver += silverLog.Amount;
                _silverByLocation[silverLog.Location] = _silverByLocation.GetValueOrDefault(silverLog.Location) + silverLog.Amount;
            }
        });
    }

    private void Safely(Action update)
    {
        try
        {
            update();
            OnChanged?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // Handlers on IGatheringSessionService's events must never throw past this boundary -
            // they run inline from GatheringEventRouter's fire-and-forget dispatch, and an
            // unhandled exception here would be lost as an unobserved task exception anyway. A
            // failed UI-state update is preferable to destabilizing the gathering pipeline.
        }
    }
}
