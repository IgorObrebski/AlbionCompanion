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
// NOT wired yet: fame gain. UpdateFame (code 82, per Nouuu/Albion-Online-OpenRadar) never
// actually fired in any of our capture sessions, including one where the player visibly received
// a fame popup - so its parameter layout is unconfirmed. Wiring it blind risks silently recording
// wrong numbers, which is worse than not recording fame at all yet.
//
// itemId is currently the raw numeric resource-type id, not a human-readable name (e.g. "T4_ORE")
// - see specs/albion-companion-context.md's ItemDictionary/ao-bin-dumps import, still to be built.
public class GatheringEventRouter
{
    private const byte SemanticEventCodeParameterKey = 252;
    private const byte HarvestItemIdParameterKey = 4;
    private const int PerSwingAmountApproximation = 1;

    private readonly IGatheringSessionService _sessionService;

    public GatheringEventRouter(IPhotonParser photonParser, IGatheringSessionService sessionService)
    {
        _sessionService = sessionService;
        photonParser.OnEventReceived += (_, e) => _ = HandleEventAsync(e);
    }

    internal Task HandleEventAsync(PhotonEvent photonEvent)
    {
        if (!photonEvent.Parameters.TryGetValue(SemanticEventCodeParameterKey, out var semanticCodeValue) ||
            semanticCodeValue is null)
        {
            return Task.CompletedTask;
        }

        var semanticCode = Convert.ToByte(semanticCodeValue);

        return semanticCode == (byte)AlbionEventCode.HarvestStart
            ? HandleHarvestStartAsync(photonEvent)
            : Task.CompletedTask;
    }

    private Task HandleHarvestStartAsync(PhotonEvent photonEvent)
    {
        if (!photonEvent.Parameters.TryGetValue(HarvestItemIdParameterKey, out var itemIdValue) || itemIdValue is null)
        {
            return Task.CompletedTask;
        }

        var itemId = Convert.ToString(itemIdValue) ?? string.Empty;

        return _sessionService.AddItemAsync(itemId, PerSwingAmountApproximation);
    }
}
