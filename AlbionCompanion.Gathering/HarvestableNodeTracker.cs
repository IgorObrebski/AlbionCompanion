using AlbionCompanion.Sniffer.AlbionEvents;
using AlbionCompanion.Sniffer.Protocol16;

namespace AlbionCompanion.Gathering;

// Caches each harvestable node's tier by node id, from NewHarvestableObject (code 40)
// broadcasts. HarvestStart (code 59) carries the node id and the resource's category code, but
// not its tier - the tier only appears in the node's own spawn/visibility broadcast, keyed by
// the same node id (parameter 0 there, parameter 3 in HarvestStart - confirmed via live capture).
// GatheringEventRouter joins the two to build a real item identifier like "T4_ORE" instead of
// just the bare category code, which otherwise conflates every tier of a resource together
// (e.g. Iron/Tin/Titanium would all just look like "Ore" with no tier distinction).
public interface IHarvestableNodeTracker
{
    int? GetTier(int nodeId);
}

public class HarvestableNodeTracker : IHarvestableNodeTracker
{
    private const byte SemanticEventCodeParameterKey = 252;
    private const byte NodeIdParameterKey = 0;
    private const byte TierParameterKey = 7;

    private readonly Dictionary<int, int> _tierByNodeId = new();

    public HarvestableNodeTracker(IPhotonParser photonParser)
    {
        photonParser.OnEventReceived += (_, e) => Handle(e);
    }

    public int? GetTier(int nodeId) => _tierByNodeId.TryGetValue(nodeId, out var tier) ? tier : null;

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

        _tierByNodeId[Convert.ToInt32(nodeIdValue)] = Convert.ToInt32(tierValue);
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
