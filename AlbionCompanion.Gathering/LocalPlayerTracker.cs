using AlbionCompanion.Sniffer.Protocol16;

namespace AlbionCompanion.Gathering;

// Broadcast events like HarvestStart are visible to every client in the zone, not just the
// local player's own actions (confirmed via live capture on 2026-07-16: a HarvestFinished-driven
// gathering session recorded item types the player never actually mined - two other nearby
// players' harvest swings had been captured too). GatheringEventRouter needs to filter those out
// by comparing an event's actor id against the local player's own current entity id.
//
// The same zone-join response ZoneTracker already parses (PhotonResponse parameter 253 == 2)
// carries the local player's own entity id for the new zone in parameter 0 - this is NOT a
// stable global player id, it's reassigned on every zone change (confirmed: the same player,
// same character name, had two different parameter-0 values across two different zone joins).
public class LocalPlayerTracker : ILocalPlayerTracker
{
    private const byte ZoneJoinSubCodeKey = 253;
    private const byte ZoneJoinSubCode = 2;
    private const byte OwnEntityIdParameterKey = 0;

    public int? CurrentEntityId { get; private set; }

    public LocalPlayerTracker(IPhotonParser photonParser)
    {
        photonParser.OnResponseReceived += (_, response) => Handle(response);
    }

    internal void Handle(PhotonResponse response)
    {
        if (!response.Parameters.TryGetValue(ZoneJoinSubCodeKey, out var subCode) ||
            Convert.ToInt32(subCode) != ZoneJoinSubCode)
        {
            return;
        }

        if (response.Parameters.TryGetValue(OwnEntityIdParameterKey, out var entityIdValue) && entityIdValue is not null)
        {
            CurrentEntityId = Convert.ToInt32(entityIdValue);
        }
    }
}
