using AlbionCompanion.Sniffer.AlbionEvents;
using AlbionCompanion.Sniffer.Protocol16;

namespace AlbionCompanion.Gathering;

// Caches each harvestable node's tier and enchantment level by node id, from
// NewHarvestableObject (code 40) broadcasts. HarvestStart (code 59) carries the node id and the
// resource's category code, but neither its tier nor enchantment level - those only appear in the
// node's own spawn/visibility broadcast, keyed by the same node id (parameter 0 there, parameter 3
// in HarvestStart - confirmed via live capture). GatheringEventRouter joins the two to build a
// real item identifier like "T4_ORE" instead of just the bare category code, which otherwise
// conflates every tier of a resource together (e.g. Iron/Tin/Titanium would all just look like
// "Ore" with no tier distinction).
//
// Enchantment level (parameter 11) confirmed via live capture on 2026-08-02: it stays constant
// across repeated broadcasts for the same node id while other parameters (e.g. parameter 10,
// likely remaining charges) change over time, and cross-referencing against the player's own
// manually-tallied gathering (Titanium ench.2 x7, Iron ench.1 x6, Titanium ench.1 x1) matched
// parameter 11's value exactly for every node harvested in that session. 0 means unenchanted,
// matching ao-bin-dumps items.json's convention of the bare "T{tier}_{CATEGORY}" UniqueName (no
// "_LEVEL{n}@{n}" suffix) for enchant level 0.
public interface IHarvestableNodeTracker
{
    int? GetTier(int nodeId);
    int? GetEnchantmentLevel(int nodeId);
}

public class HarvestableNodeTracker : IHarvestableNodeTracker
{
    private const byte SemanticEventCodeParameterKey = 252;
    private const byte NodeIdParameterKey = 0;
    private const byte TierParameterKey = 7;
    private const byte EnchantmentLevelParameterKey = 11;

    private readonly Dictionary<int, int> _tierByNodeId = new();
    private readonly Dictionary<int, int> _enchantmentLevelByNodeId = new();

    public HarvestableNodeTracker(IPhotonParser photonParser)
    {
        photonParser.OnEventReceived += (_, e) => Handle(e);
    }

    public int? GetTier(int nodeId) => _tierByNodeId.TryGetValue(nodeId, out var tier) ? tier : null;

    public int? GetEnchantmentLevel(int nodeId) =>
        _enchantmentLevelByNodeId.TryGetValue(nodeId, out var level) ? level : null;

    internal void Handle(PhotonEvent photonEvent)
    {
        if (!photonEvent.Parameters.TryGetValue(SemanticEventCodeParameterKey, out var semanticCodeValue) ||
            semanticCodeValue is null || !TryToByte(semanticCodeValue, out var semanticCode) ||
            semanticCode != (byte)AlbionEventCode.NewHarvestableObject)
        {
            return;
        }

        if (!photonEvent.Parameters.TryGetValue(NodeIdParameterKey, out var nodeIdValue) || nodeIdValue is null ||
            !photonEvent.Parameters.TryGetValue(TierParameterKey, out var tierValue) || tierValue is null)
        {
            return;
        }

        var nodeId = Convert.ToInt32(nodeIdValue);
        _tierByNodeId[nodeId] = Convert.ToInt32(tierValue);

        // Unlike tier, enchantment level isn't always present on every broadcast shape (e.g. the
        // non-resource "living creature" broadcasts noted in HarvestableCategory) - default to 0
        // (unenchanted) rather than leaving the node unresolved.
        _enchantmentLevelByNodeId[nodeId] = photonEvent.Parameters.TryGetValue(EnchantmentLevelParameterKey, out var enchantValue) && enchantValue is not null
            ? Convert.ToInt32(enchantValue)
            : 0;
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
}
