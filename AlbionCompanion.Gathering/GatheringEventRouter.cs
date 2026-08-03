using AlbionCompanion.Sniffer.AlbionEvents;
using AlbionCompanion.Sniffer.Protocol16;

namespace AlbionCompanion.Gathering;

// Feeds real gathering activity into IGatheringSessionService. Field layout below is inferred
// from live captures (see conversation history / debug_packets.log), not from any official
// schema.
//
// Keyed off HarvestFinished (code 61), not HarvestStart (59) - reversing the 2026-07-16 decision.
// That decision assumed HarvestFinished only fires on full node depletion; a controlled live-
// capture experiment on 2026-08-02 disproved this: HarvestFinished fires after every individual
// swing. Two problems with the old HarvestStart-based approach motivated the switch:
//   1. Timing: HarvestStart fires when a swing *begins*, so interrupting a swing mid-animation
//      (e.g. moving away) still recorded +1, even though nothing landed in the inventory.
//   2. Amount: HarvestStart carries no yield amount at all, so every swing was hardcoded to +1 -
//      wrong for gear/Focus/specialization bonuses that grant more than 1 unit per swing (the
//      player explicitly flagged this - "gathering level" bonuses can't be assumed away as flat).
// HarvestFinished parameter 5 is the real per-swing base yield, confirmed by a controlled
// experiment: the player watched their exact resource count in-game across three swings on one
// node (deltas +2, +2, +2) - parameter 5 read 2, 2, 2 across the matching three HarvestFinished
// events, not the naive assumption of always 1. A second experiment deliberately interrupted the
// third of three swings (deltas +2, +2, +0) - the first two HarvestFinished events again showed
// parameter 5 = 2, and the third had parameter 5 *absent* entirely (Photon omits parameters at
// their default value - 0 here), matching the real zero gain exactly. A third experiment isolated
// a gathering-specialization bonus proc (deltas +2, +2, +4): the bonus swing additionally had
// parameter 6 = 2 (the first two had no parameter 6 at all) - the bonus rides in its own
// parameter, not folded into parameter 5, so real yield is parameter 5 + parameter 6.
// AddItemAsync is skipped entirely when the total is absent or non-positive, so an interrupted
// swing correctly adds nothing.
// (An earlier, wrong hypothesis considered parameter 8 for the base amount - it also varies per
// swing, but a clean unbroken 2/2/2 experiment showed it decrementing 2,1,0 regardless, meaning it
// tracks something else, most likely the node's remaining charges - not yield.)
//
// Item identity: parameter 4 of HarvestFinished is the exact numeric "Index" ao-bin-dumps
// items.json assigns per UniqueName (confirmed 2026-08-02 by cross-referencing several live swings
// against the real items.json - e.g. 978 -> "T5_ORE_LEVEL2@2", 1022 -> "T4_FIBER", 972 ->
// "T4_ORE_LEVEL1@1", every one matching exactly what the player was actually gathering at that
// moment). This already encodes tier, category, AND enchantment level in one value - a huge
// simplification over the previous approach (composing "T{tier}_{CATEGORY}" from a node-id keyed
// cache fed by NewHarvestableObject/HarvestStart broadcasts, which needed to have seen a signal
// for that specific node first - useless right after an app restart for any node the player had
// already visited in a prior process, since NewHarvestableObject is a one-time "entered view
// range" broadcast that won't re-fire for an already-loaded node). IHarvestableNodeTracker and its
// node-id-keyed tier/category/enchantment caching have been removed entirely - nothing needs them
// anymore. If the index isn't in IItemDictionaryService (e.g. seeding failed, or this is a rare
// item type not covered by testing), the fallback is the bare numeric index, an approximate id
// beating dropping the swing.
//
// Fame gain: UpdateFame (code 82) confirmed via live capture on 2026-07-18 mining Iron (T4 Ore).
// Parameter 0 is the earning character's own entity id (broadcast is self-only in every sample
// seen, but filtered against CurrentEntityId anyway for the same reason HarvestFinished is - no
// counter-example seen yet doesn't mean one can't exist). Parameter 1 is the account's *running
// total* fame (monotonically increasing across events); parameter 2 is this event's own delta -
// confirmed by checking successive samples: e.g. 3388810500 -> 3389410500 is a delta of 600000,
// exactly matching that second event's own parameter 2. The wire value is scaled 10000x from the
// in-game display number (600000 / 10000 = 60 fame - plausible for a single T4 Ore swing), so
// AddFameAsync divides by FameScaleFactor to store the human-readable number FameLog.Amount and
// GatheringSession.TotalFameEarned are meant to hold.
//
// Filters by actor: HarvestFinished is broadcast to every client in the zone, not just the
// player's own actions (confirmed via live capture, same as HarvestStart before it - a session
// recorded two other players' harvest swings on different resource types alongside the player's
// own). Parameter 0 is the harvesting character's own entity id, checked against
// ILocalPlayerTracker.CurrentEntityId.
//
// Silver gain: UpdateMoney (code 81) confirmed via live capture on 2026-08-03, auto-looted silver
// from a mob kill. Unlike UpdateFame, there is no separate delta parameter - parameter 1 is only
// the account's running total silver balance (scaled 10000x, same convention as fame), confirmed
// by cross-referencing two live samples against the player's own in-game silver display: total
// 2504384241 -> displayed 250438, then 2505306831 -> displayed 250530 after a second kill (delta
// 922590 / 10000 = 92.26, matching the real +92 gain to within the same small fractional residue
// both raw samples showed against their own displayed integer - the game's internal ledger
// apparently keeps sub-silver precision the UI truncates, not a decoding error). Delta is computed
// here by diffing against the last-seen raw total (_lastKnownSilverTotal) rather than trusting a
// wire-provided delta, since none exists. The very first sighting only seeds the baseline - it
// can't have a "delta" against nothing without wrongly reporting the player's entire prior wealth
// as a single gain from this session. Parameter 0 (actor entity id) is filtered the same way as
// HarvestFinished/UpdateFame; confirmed the entity id can differ across the two samples above
// (2861 -> 41390, a zone-transition entity-id reset - same class of behavior LocalPlayerTracker
// already handles), so the running-total baseline is intentionally NOT reset just because a given
// reading's actor didn't match - only skip that one reading, keep the last confirmed baseline.
public class GatheringEventRouter
{
    private const byte SemanticEventCodeParameterKey = 252;
    private const byte HarvestActorEntityIdParameterKey = 0;
    private const byte HarvestItemIndexParameterKey = 4;
    private const byte HarvestFinishedAmountParameterKey = 5;
    private const byte HarvestFinishedBonusAmountParameterKey = 6;
    private const byte FameActorEntityIdParameterKey = 0;
    private const byte FameDeltaParameterKey = 2;
    private const int FameScaleFactor = 10000;
    private const string GatheringFameType = "Gathering";
    private const byte SilverActorEntityIdParameterKey = 0;
    private const byte SilverTotalParameterKey = 1;
    private const int SilverScaleFactor = 10000;

    private long? _lastKnownSilverTotal;

    private readonly IGatheringSessionService _sessionService;
    private readonly ILocalPlayerTracker _localPlayerTracker;
    private readonly IItemDictionaryService _itemDictionary;

    // Surfaces any exception from AddItemAsync instead of letting it vanish as an unobserved
    // fire-and-forget task fault. photonParser.OnEventReceived's subscriber lambda below discards
    // the Task this method returns (matches the ZoneTracker.OnError / RawEventRecorder.OnRecordFailure
    // pattern) - without this, a scoped AppDbContext concurrency exception (a real risk: HarvestFinished
    // can fire in rapid bursts, each triggering an AddItemAsync call against the same shared
    // AppDbContext instance ZoneTracker also writes through) would silently drop every gathered item
    // for the rest of the run with zero trace in any log.
    public event EventHandler<Exception>? OnError;

    public GatheringEventRouter(
        IPhotonParser photonParser,
        IGatheringSessionService sessionService,
        ILocalPlayerTracker localPlayerTracker,
        IItemDictionaryService itemDictionary)
    {
        _sessionService = sessionService;
        _localPlayerTracker = localPlayerTracker;
        _itemDictionary = itemDictionary;
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
            // packet (see AlbionPhotonParser.RaiseIsolated) - a value this large can't be a known
            // semantic code anyway, so just skip it instead.
            if (!TryToByte(semanticCodeValue, out var semanticCode))
            {
                return;
            }

            if (semanticCode == (byte)AlbionEventCode.HarvestFinished)
            {
                await HandleHarvestFinishedAsync(photonEvent);
            }
            else if (semanticCode == (byte)AlbionEventCode.UpdateFame)
            {
                await HandleUpdateFameAsync(photonEvent);
            }
            else if (semanticCode == (byte)AlbionEventCode.UpdateMoney)
            {
                await HandleUpdateMoneyAsync(photonEvent);
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

    private async Task HandleHarvestFinishedAsync(PhotonEvent photonEvent)
    {
        if (!photonEvent.Parameters.TryGetValue(HarvestActorEntityIdParameterKey, out var actorIdValue) || actorIdValue is null)
        {
            return;
        }

        // Unknown local entity id (e.g. before the first zone-join response arrives) means we
        // can't confirm this swing is the player's own - skip rather than risk misattributing it.
        if (_localPlayerTracker.CurrentEntityId is not { } localEntityId || Convert.ToInt32(actorIdValue) != localEntityId)
        {
            return;
        }

        // Absent means Photon omitted it at its default value (0) - an interrupted swing that
        // yielded nothing. Not an error, just nothing to record.
        if (!photonEvent.Parameters.TryGetValue(HarvestFinishedAmountParameterKey, out var amountValue) || amountValue is null)
        {
            return;
        }

        var amount = Convert.ToInt32(amountValue);
        if (photonEvent.Parameters.TryGetValue(HarvestFinishedBonusAmountParameterKey, out var bonusValue) && bonusValue is not null)
        {
            amount += Convert.ToInt32(bonusValue);
        }

        if (amount <= 0)
        {
            return;
        }

        if (!photonEvent.Parameters.TryGetValue(HarvestItemIndexParameterKey, out var indexValue) || indexValue is null)
        {
            return;
        }

        var itemId = await ResolveItemIdAsync(Convert.ToInt32(indexValue));

        await _sessionService.AddItemAsync(itemId, amount);
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

    private async Task HandleUpdateMoneyAsync(PhotonEvent photonEvent)
    {
        if (!photonEvent.Parameters.TryGetValue(SilverActorEntityIdParameterKey, out var actorIdValue) || actorIdValue is null)
        {
            return;
        }

        if (_localPlayerTracker.CurrentEntityId is not { } localEntityId || Convert.ToInt32(actorIdValue) != localEntityId)
        {
            return;
        }

        if (!photonEvent.Parameters.TryGetValue(SilverTotalParameterKey, out var totalValue) || totalValue is null)
        {
            return;
        }

        var rawTotal = Convert.ToInt64(totalValue);

        if (_lastKnownSilverTotal is not { } lastTotal)
        {
            _lastKnownSilverTotal = rawTotal;
            return;
        }

        _lastKnownSilverTotal = rawTotal;

        var delta = rawTotal - lastTotal;
        if (delta <= 0)
        {
            return;
        }

        var silverAmount = (int)(delta / SilverScaleFactor);
        if (silverAmount <= 0)
        {
            return;
        }

        await _sessionService.AddSilverAsync(silverAmount);
    }

    private async Task<string> ResolveItemIdAsync(int itemIndex)
    {
        var entry = await _itemDictionary.GetItemByIndexAsync(itemIndex);

        // Fall back to the bare numeric index if it's not in the dictionary (e.g. seeding failed,
        // or a genuinely uncovered item type) - an approximate id beats silently dropping the swing.
        return entry?.UniqueName ?? itemIndex.ToString();
    }
}
