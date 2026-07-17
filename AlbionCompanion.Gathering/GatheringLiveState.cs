namespace AlbionCompanion.Gathering;

public class GatheringLiveState : IGatheringLiveState
{
    private readonly Dictionary<string, int> _itemTotals = new();

    public bool IsActive { get; private set; }
    public string? StartLocation { get; private set; }
    public int TotalFame { get; private set; }
    public IReadOnlyDictionary<string, int> ItemTotals => _itemTotals;

    public event EventHandler? OnChanged;

    public void Attach(IGatheringSessionService sessionService)
    {
        sessionService.OnSessionStarted += (_, session) => Safely(() =>
        {
            _itemTotals.Clear();
            TotalFame = 0;
            StartLocation = session.StartLocation;
            IsActive = true;
        });

        sessionService.OnSessionEnded += (_, _) => Safely(() =>
        {
            IsActive = false;
        });

        sessionService.OnItemAdded += (_, item) => Safely(() =>
        {
            var itemId = item.ItemId;
            var amount = item.Amount;
            _itemTotals[itemId] = _itemTotals.GetValueOrDefault(itemId) + amount;
        });

        sessionService.OnFameAdded += (_, fameLog) => Safely(() =>
        {
            TotalFame += fameLog.Amount;
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
