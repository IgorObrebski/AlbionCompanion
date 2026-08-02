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
// HarvestFinished parameter 5 is the real per-swing yield, confirmed by a controlled experiment:
// the player watched their exact resource count in-game across three swings on one node (deltas
// +2, +2, +2) - parameter 5 read 2, 2, 2 across the matching three HarvestFinished events, not the
// naive assumption of always 1. A second experiment deliberately interrupted the third of three
// swings (deltas +2, +2, +0) - the first two HarvestFinished events again showed parameter 5 = 2,
// and the third had parameter 5 *absent* entirely (Photon omits parameters at their default value
// - 0 here), matching the real zero gain exactly. AddItemAsync is skipped entirely when parameter
// 5 is absent or non-positive, so an interrupted swing correctly adds nothing.
// (An earlier, wrong hypothesis considered parameter 8 for this - it also varies per swing, but a
// clean unbroken 2/2/2 experiment showed it decrementing 2,1,0 regardless, meaning it tracks
// something else, most likely the node's remaining charges - not yield.)
// A third experiment isolated a gathering-specialization bonus proc (deltas +2, +2, +4): the first
// two HarvestFinished events had parameter 5 = 2 and no parameter 6 at all (equivalent to 0); the
// bonus swing had parameter 5 = 2 *and* parameter 6 = 2 - the bonus rides in a separate parameter,
// not folded into parameter 5, so real yield is parameter 5 + parameter 6.
//
// Unlike HarvestStart, HarvestFinished does NOT carry the resource's category code (parameter 4
// there is some other value, not a HarvestableCategory-range code) - only the node id (parameter
// 3, same position as HarvestStart's). Category, tier, and enchantment level are all resolved
// through IHarvestableNodeTracker's NewHarvestableObject cache instead (see that class) - if the
// node's spawn broadcast was never captured, none of the three can be resolved and the item id
// falls back to the bare node id, an approximate id beating dropping the swing.
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
// itemId is built as "T{tier}_{CATEGORY}" (e.g. "T4_ORE"), or "T{tier}_{CATEGORY}_LEVEL{n}@{n}"
// for an enchanted resource (matching ao-bin-dumps items.json's real UniqueName convention) - this
// is still the resource's UniqueName, not a localized display name, see
// specs/albion-companion-context.md's ItemDictionary/ao-bin-dumps items.json import for that.
//
// Filters by actor: HarvestFinished is broadcast to every client in the zone, not just the
// player's own actions (confirmed via live capture, same as HarvestStart before it - a session
// recorded two other players' harvest swings on different resource types alongside the player's
// own). Parameter 0 is the harvesting character's own entity id, checked against
// ILocalPlayerTracker.CurrentEntityId.
public class GatheringEventRouter
{
    private const byte SemanticEventCodeParameterKey = 252;
    private const byte HarvestActorEntityIdParameterKey = 0;
    private const byte HarvestNodeIdParameterKey = 3;
    private const byte HarvestFinishedAmountParameterKey = 5;
    private const byte HarvestFinishedBonusAmountParameterKey = 6;
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
    // pattern) - without this, a scoped AppDbContext concurrency exception (a real risk: HarvestFinished
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

    private Task HandleHarvestFinishedAsync(PhotonEvent photonEvent)
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

        // Absent means Photon omitted it at its default value (0) - an interrupted swing that
        // yielded nothing. Not an error, just nothing to record.
        if (!photonEvent.Parameters.TryGetValue(HarvestFinishedAmountParameterKey, out var amountValue) || amountValue is null)
        {
            return Task.CompletedTask;
        }

        // Confirmed via live capture 2026-08-02: a swing that rolls a gathering-specialization
        // bonus reports the base amount in parameter 5 and the bonus separately in parameter 6
        // (0 - and typically omitted from the wire entirely - when no bonus procs). A controlled
        // test where the player watched their exact resource count (deltas +2, +2, +4) matched
        // parameter 5 = 2 on every swing and parameter 6 = 2 only on the bonus swing (2+2=4).
        var amount = Convert.ToInt32(amountValue);
        if (photonEvent.Parameters.TryGetValue(HarvestFinishedBonusAmountParameterKey, out var bonusValue) && bonusValue is not null)
        {
            amount += Convert.ToInt32(bonusValue);
        }

        if (amount <= 0)
        {
            return Task.CompletedTask;
        }

        if (!photonEvent.Parameters.TryGetValue(HarvestNodeIdParameterKey, out var nodeIdValue) || nodeIdValue is null)
        {
            return Task.CompletedTask;
        }

        var itemId = ResolveItemId(Convert.ToInt32(nodeIdValue));

        return _sessionService.AddItemAsync(itemId, amount);
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

    private string ResolveItemId(int nodeId)
    {
        var categoryCode = _nodeTracker.GetCategoryCode(nodeId);
        var category = categoryCode is { } code ? HarvestableCategory.FromTypeCode(code) : null;
        var tier = _nodeTracker.GetTier(nodeId);

        // Fall back to the bare node id if we can't resolve a full "T{tier}_{CATEGORY}" id (e.g.
        // the node's spawn broadcast was never captured, or the category code is out of every
        // known range) - an approximate item id beats silently dropping the swing.
        if (category is null || tier is null)
        {
            return nodeId.ToString();
        }

        // Matches ao-bin-dumps items.json's real UniqueName convention for enchanted resources
        // (e.g. "T4_ORE_LEVEL2@2" = Rare Iron Ore) - confirmed via live capture on 2026-08-02 (see
        // HarvestableNodeTracker). Enchant level 0 (unenchanted) uses the bare id with no suffix.
        var enchantmentLevel = _nodeTracker.GetEnchantmentLevel(nodeId) ?? 0;
        return enchantmentLevel > 0
            ? $"T{tier}_{category}_LEVEL{enchantmentLevel}@{enchantmentLevel}"
            : $"T{tier}_{category}";
    }
}
