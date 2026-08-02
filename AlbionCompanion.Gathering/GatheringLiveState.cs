namespace AlbionCompanion.Gathering;

public class GatheringLiveState : IGatheringLiveState
{
    // Mutated on the packet-capture/parse thread (via GatheringEventRouter's fire-and-forget event
    // dispatch), read and enumerated on the Blazor UI thread (Home.razor's render). _lock protects
    // every access to the mutable fields below; ItemTotals hands out an immutable snapshot copy
    // rather than the live dictionary, so a UI-thread enumeration can never race a capture-thread
    // mutation (Dictionary<> itself does not support concurrent read/write).
    private readonly object _lock = new();
    private readonly Dictionary<(string ItemId, string Location), int> _itemTotals = new();

    private bool _isActive;
    private string? _startLocation;
    private int _totalFame;

    public bool IsActive
    {
        get { lock (_lock) { return _isActive; } }
    }

    public string? StartLocation
    {
        get { lock (_lock) { return _startLocation; } }
    }

    public int TotalFame
    {
        get { lock (_lock) { return _totalFame; } }
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

    public event EventHandler? OnChanged;

    public async Task Attach(IGatheringSessionService sessionService)
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
                _totalFame = snapshot.TotalFameEarned;
                foreach (var total in snapshot.ItemTotals)
                {
                    _itemTotals[(total.ItemId, total.Location)] = total.Amount;
                }
            }

            OnChanged?.Invoke(this, EventArgs.Empty);
        }

        sessionService.OnSessionStarted += (_, session) => Safely(() =>
        {
            lock (_lock)
            {
                _itemTotals.Clear();
                _totalFame = 0;
                _startLocation = session.StartLocation;
                _isActive = true;
            }
        });

        sessionService.OnSessionEnded += (_, _) => Safely(() =>
        {
            lock (_lock)
            {
                _isActive = false;
            }
        });

        sessionService.OnItemAdded += (_, item) => Safely(() =>
        {
            var key = (item.ItemId, item.Location);
            var amount = item.Amount;
            lock (_lock)
            {
                _itemTotals[key] = _itemTotals.GetValueOrDefault(key) + amount;
            }
        });

        sessionService.OnFameAdded += (_, fameLog) => Safely(() =>
        {
            lock (_lock)
            {
                _totalFame += fameLog.Amount;
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
