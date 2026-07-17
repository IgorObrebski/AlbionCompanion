namespace AlbionCompanion.Gathering;

public class GatheringLiveState : IGatheringLiveState
{
    // Mutated on the packet-capture/parse thread (via GatheringEventRouter's fire-and-forget event
    // dispatch), read and enumerated on the Blazor UI thread (Home.razor's render). _lock protects
    // every access to the mutable fields below; ItemTotals hands out an immutable snapshot copy
    // rather than the live dictionary, so a UI-thread enumeration can never race a capture-thread
    // mutation (Dictionary<> itself does not support concurrent read/write).
    private readonly object _lock = new();
    private readonly Dictionary<string, int> _itemTotals = new();

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

    public IReadOnlyDictionary<string, int> ItemTotals
    {
        get { lock (_lock) { return new Dictionary<string, int>(_itemTotals); } }
    }

    public event EventHandler? OnChanged;

    public void Attach(IGatheringSessionService sessionService)
    {
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
            var itemId = item.ItemId;
            var amount = item.Amount;
            lock (_lock)
            {
                _itemTotals[itemId] = _itemTotals.GetValueOrDefault(itemId) + amount;
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
