using AlbionCompanion.Sniffer.AlbionEvents;
using AlbionCompanion.Sniffer.Protocol16;

namespace AlbionCompanion.Gathering;

// Feeds real gathering activity into IGatheringSessionService. Field layout below is inferred
// from live captures (see conversation history / debug_packets.log), not from any official
// schema.
//
// Keyed off HarvestStart (code 59), not HarvestFinished (61): confirmed via live capture on
// 2026-07-16 that HarvestFinished only fires when a resource node's charges are fully depleted
// to zero, not on every individual swing/action - so a session where the player mines a node
// down from 2 charges to 0 (starting mid-depletion, a very common case) or stops partway through
// a node never sees a HarvestFinished at all. HarvestStart fires on every swing regardless, and
// already carries the item's numeric type id in parameter 4.
//
// Trade-off: HarvestStart doesn't carry a harvested amount (only HarvestFinished did, when it
// fires). Each swing is recorded as amount=1 rather than the node's actual per-swing yield -
// this is a deliberate approximation, not a bug: real yield varies with gear/Focus/luck
// duplication anyway, which the player has already flagged as unreliable for analysis purposes.
//
// Fame gain: UpdateFame (code 82) confirmed via live capture on 2026-07-18 mining Iron (T4 Ore).
// Parameter 0 is the earning character's own entity id (broadcast is self-only in every sample
// seen, but filtered against CurrentEntityId anyway for the same reason HarvestStart is - no
// counter-example seen yet doesn't mean one can't exist). Parameter 1 is the account's *running
// total* fame (monotonically increasing across events); parameter 2 is this event's own delta -
// confirmed by checking successive samples: e.g. 3388810500 -> 3389410500 is a delta of 600000,
// exactly matching that second event's own parameter 2. The wire value is scaled 10000x from the
// in-game display number (600000 / 10000 = 60 fame - plausible for a single T4 Ore swing), so
// AddFameAsync divides by FameScaleFactor to store the human-readable number FameLog.Amount and
// GatheringSession.TotalFameEarned are meant to hold.
//
// itemId is built as "T{tier}_{CATEGORY}" (e.g. "T4_ORE") by joining HarvestStart's category
// code with the node's tier from IHarvestableNodeTracker - confirmed necessary via live capture:
// a session where the player mined Iron/Tin/Titanium (T4/T3/T5 Ore) all showed the *same*
// category code, because that code alone doesn't distinguish tier (see HarvestableCategory).
// This is still the resource's UniqueName, not a localized display name - see
// specs/albion-companion-context.md's ItemDictionary/ao-bin-dumps items.json import for that.
//
// Filters by actor: HarvestStart is broadcast to every client in the zone, not just the player's
// own actions (confirmed via live capture - a session recorded two other players' harvest swings
// on different resource types alongside the player's own). Parameter 0 is the harvesting
// character's own entity id, checked against ILocalPlayerTracker.CurrentEntityId.
public class GatheringEventRouter
{
    private const byte SemanticEventCodeParameterKey = 252;
    private const byte HarvestActorEntityIdParameterKey = 0;
    private const byte HarvestNodeIdParameterKey = 3;
    private const byte HarvestCategoryCodeParameterKey = 4;
    private const int PerSwingAmountApproximation = 1;
    private const byte FameActorEntityIdParameterKey = 0;
    private const byte FameDeltaParameterKey = 2;
    private const int FameScaleFactor = 10000;
    private const string GatheringFameType = "Gathering";

    private readonly IGatheringSessionService _sessionService;
    private readonly ILocalPlayerTracker _localPlayerTracker;
    private readonly IHarvestableNodeTracker _nodeTracker;

    // Surfaces any exception from AddItemAsync instead of letting it vanish as an unobserved
    // fire-and-forget task fault. photonParser.OnEventReceived's subscriber lambda below discards
    // the Task this method returns (matches the ZoneTracker.OnError / RawEventRecorder.OnRecordFailure
    // pattern) - without this, a scoped AppDbContext concurrency exception (a real risk: HarvestStart
    // can fire in rapid bursts, each triggering an AddItemAsync call against the same shared
    // AppDbContext instance ZoneTracker also writes through) would silently drop every gathered item
    // for the rest of the run with zero trace in any log.
    public event EventHandler<Exception>? OnError;

    public GatheringEventRouter(
        IPhotonParser photonParser,
        IGatheringSessionService sessionService,
        ILocalPlayerTracker localPlayerTracker,
        IHarvestableNodeTracker nodeTracker)
    {
        _sessionService = sessionService;
        _localPlayerTracker = localPlayerTracker;
        _nodeTracker = nodeTracker;
        photonParser.OnEventReceived += (_, e) => _ = HandleEventAsync(e);
    }

    internal async Task HandleEventAsync(PhotonEvent photonEvent)
    {
        try
        {
            if (!photonEvent.Parameters.TryGetValue(SemanticEventCodeParameterKey, out var semanticCodeValue) ||
                semanticCodeValue is null)
            {
                return;
            }

            // Confirmed via live capture: Convert.ToByte throws OverflowException when this
            // parameter decodes to a value outside 0-255 (it isn't always a small "code" byte -
            // depends on which Photon type encoded it on the wire). A throw here propagates out of
            // the live Photon parse loop and aborts every other command bundled in the same UDP
            // packet (see AlbionPhotonParser.RaiseIsolated) - a value this large can't be
            // AlbionEventCode.HarvestStart anyway, so just skip it instead.
            if (!TryToByte(semanticCodeValue, out var semanticCode))
            {
                return;
            }

            if (semanticCode == (byte)AlbionEventCode.HarvestStart)
            {
                await HandleHarvestStartAsync(photonEvent);
            }
            else if (semanticCode == (byte)AlbionEventCode.UpdateFame)
            {
                await HandleUpdateFameAsync(photonEvent);
            }
        }
        catch (Exception ex)
        {
            OnError?.Invoke(this, ex);
        }
    }

    private static bool TryToByte(object value, out byte result)
    {
        var numeric = Convert.ToInt64(value);
        if (numeric is >= byte.MinValue and <= byte.MaxValue)
        {
            result = (byte)numeric;
            return true;
        }

        result = 0;
        return false;
    }

    private Task HandleHarvestStartAsync(PhotonEvent photonEvent)
    {
        if (!photonEvent.Parameters.TryGetValue(HarvestActorEntityIdParameterKey, out var actorIdValue) || actorIdValue is null)
        {
            return Task.CompletedTask;
        }

        // Unknown local entity id (e.g. before the first zone-join response arrives) means we
        // can't confirm this swing is the player's own - skip rather than risk misattributing it.
        if (_localPlayerTracker.CurrentEntityId is not { } localEntityId || Convert.ToInt32(actorIdValue) != localEntityId)
        {
            return Task.CompletedTask;
        }

        if (!photonEvent.Parameters.TryGetValue(HarvestCategoryCodeParameterKey, out var categoryCodeValue) || categoryCodeValue is null)
        {
            return Task.CompletedTask;
        }

        var itemId = ResolveItemId(photonEvent, Convert.ToInt32(categoryCodeValue));

        return _sessionService.AddItemAsync(itemId, PerSwingAmountApproximation);
    }

    private async Task HandleUpdateFameAsync(PhotonEvent photonEvent)
    {
        if (!photonEvent.Parameters.TryGetValue(FameActorEntityIdParameterKey, out var actorIdValue) || actorIdValue is null)
        {
            return;
        }

        if (_localPlayerTracker.CurrentEntityId is not { } localEntityId || Convert.ToInt32(actorIdValue) != localEntityId)
        {
            return;
        }

        if (!photonEvent.Parameters.TryGetValue(FameDeltaParameterKey, out var fameDeltaValue) || fameDeltaValue is null)
        {
            return;
        }

        var fameAmount = Convert.ToInt32(fameDeltaValue) / FameScaleFactor;
        await _sessionService.AddFameAsync(GatheringFameType, fameAmount);
    }

    private string ResolveItemId(PhotonEvent photonEvent, int categoryCode)
    {
        var category = HarvestableCategory.FromTypeCode(categoryCode);
        int? tier = photonEvent.Parameters.TryGetValue(HarvestNodeIdParameterKey, out var nodeIdValue) && nodeIdValue is not null
            ? _nodeTracker.GetTier(Convert.ToInt32(nodeIdValue))
            : null;

        // Fall back to the bare numeric category code if we can't resolve a full "T{tier}_{CATEGORY}"
        // id (e.g. the node's spawn broadcast was never captured, or the category code is out of
        // every known range) - an approximate item id beats silently dropping the swing.
        return category is not null && tier is not null
            ? $"T{tier}_{category}"
            : categoryCode.ToString();
    }
}
