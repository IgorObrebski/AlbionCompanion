using AlbionCompanion.Sniffer.AlbionEvents;
using AlbionCompanion.Sniffer.Protocol16;

namespace AlbionCompanion.Gathering;

// Caches each harvestable node's category code, tier, and enchantment level by node id, from
// NewHarvestableObject (code 40) broadcasts. Neither HarvestStart (59) nor HarvestFinished (61)
// carries all three - HarvestStart has category code but not tier/enchant; HarvestFinished has
// none of the three at all, only the node id and the real yield amount (see
// GatheringEventRouter.HandleHarvestFinishedAsync) - so both handlers resolve the node's identity
// entirely through this tracker instead. Category code was confirmed live 2026-08-02 as parameter
// 5 of NewHarvestableObject (e.g. 27 for the same "ORE" range HarvestStart's own category
// parameter used) - added when GatheringEventRouter switched off HarvestStart's own category
// value to key entirely off HarvestFinished, which needed a replacement source for it.
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
    int? GetCategoryCode(int nodeId);
}

public class HarvestableNodeTracker : IHarvestableNodeTracker
{
    private const byte SemanticEventCodeParameterKey = 252;
    private const byte NodeIdParameterKey = 0;
    private const byte CategoryCodeParameterKey = 5;
    private const byte TierParameterKey = 7;
    private const byte EnchantmentLevelParameterKey = 11;

    private readonly Dictionary<int, int> _tierByNodeId = new();
    private readonly Dictionary<int, int> _enchantmentLevelByNodeId = new();
    private readonly Dictionary<int, int> _categoryCodeByNodeId = new();

    public HarvestableNodeTracker(IPhotonParser photonParser)
    {
        photonParser.OnEventReceived += (_, e) => Handle(e);
    }

    public int? GetTier(int nodeId) => _tierByNodeId.TryGetValue(nodeId, out var tier) ? tier : null;

    public int? GetEnchantmentLevel(int nodeId) =>
        _enchantmentLevelByNodeId.TryGetValue(nodeId, out var level) ? level : null;

    public int? GetCategoryCode(int nodeId) =>
        _categoryCodeByNodeId.TryGetValue(nodeId, out var category) ? category : null;

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

        if (photonEvent.Parameters.TryGetValue(CategoryCodeParameterKey, out var categoryValue) && categoryValue is not null)
        {
            _categoryCodeByNodeId[nodeId] = Convert.ToInt32(categoryValue);
        }

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
