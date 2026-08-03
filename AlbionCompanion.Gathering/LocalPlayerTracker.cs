using AlbionCompanion.Sniffer.AlbionEvents;
using AlbionCompanion.Sniffer.Protocol16;

namespace AlbionCompanion.Gathering;

// Broadcast events like HarvestStart are visible to every client in the zone, not just the
// local player's own actions (confirmed via live capture on 2026-07-16). GatheringEventRouter
// needs to filter those out by comparing an event's actor id against the local player's own
// current entity id - and, since 2026-08-03, GatheringSessionService needs the local player's
// current *character name* too, to attribute sessions to a Character.
//
// Two Photon signals feed this, both confirmed via live capture 2026-08-03:
//
// - The zone-join response (parameter 253 == 2) - already used for CurrentEntityId - is a
//   RESPONSE to our own REQUEST, so it is inherently self-only. It also carries the character's
//   nickname in parameter 2. This is the high-confidence source: both CurrentEntityId and
//   CurrentCharacterName are set from it with certainty.
// - PlayerAnnounce (semantic code 279) is a periodic EVENT that fires independent of zone
//   transitions - this is what lets CurrentEntityId recover after an app restart in the same
//   zone (the previously unfixed bug: no zone-join response means no signal at all otherwise).
//   It is NOT self-only (confirmed: two different nicknames observed for two different nearby
//   entities in one capture window), so a reading is only trusted as "us" when its nickname
//   matches either the name already confirmed via a zone-join this run (the common case - keeps
//   CurrentEntityId current as it churns), or any name in the user's registered character list
//   (the cold-start case - no zone-join has fired yet this run).
public class LocalPlayerTracker : ILocalPlayerTracker
{
    private const byte ZoneJoinSubCodeKey = 253;
    private const byte ZoneJoinSubCode = 2;
    private const byte ZoneJoinEntityIdParameterKey = 0;
    private const byte ZoneJoinNicknameParameterKey = 2;
    private const byte SemanticEventCodeParameterKey = 252;
    private const byte PlayerAnnounceEntityIdParameterKey = 0;
    private const byte PlayerAnnounceNicknameParameterKey = 2;

    private readonly ICharacterService _characterService;

    public int? CurrentEntityId { get; private set; }
    public string? CurrentCharacterName { get; private set; }

    public event EventHandler<Exception>? OnError;

    public LocalPlayerTracker(IPhotonParser photonParser, ICharacterService characterService)
    {
        _characterService = characterService;
        photonParser.OnResponseReceived += (_, response) => HandleResponse(response);
        photonParser.OnEventReceived += (_, e) => _ = HandleEventAsync(e);
    }

    internal void HandleResponse(PhotonResponse response)
    {
        if (!response.Parameters.TryGetValue(ZoneJoinSubCodeKey, out var subCode) ||
            Convert.ToInt32(subCode) != ZoneJoinSubCode)
        {
            return;
        }

        if (response.Parameters.TryGetValue(ZoneJoinEntityIdParameterKey, out var entityIdValue) && entityIdValue is not null)
        {
            CurrentEntityId = Convert.ToInt32(entityIdValue);
        }

        if (response.Parameters.TryGetValue(ZoneJoinNicknameParameterKey, out var nicknameValue) && nicknameValue is string nickname)
        {
            CurrentCharacterName = nickname;
        }
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

            if (!TryToUshort(semanticCodeValue, out var semanticCode) || semanticCode != (ushort)AlbionEventCode.PlayerAnnounce)
            {
                return;
            }

            if (!photonEvent.Parameters.TryGetValue(PlayerAnnounceNicknameParameterKey, out var nicknameValue) ||
                nicknameValue is not string nickname)
            {
                return;
            }

            if (!photonEvent.Parameters.TryGetValue(PlayerAnnounceEntityIdParameterKey, out var entityIdValue) || entityIdValue is null)
            {
                return;
            }

            var isTrustedRefresh = CurrentCharacterName is not null && nickname == CurrentCharacterName;
            var isColdStartMatch = CurrentCharacterName is null &&
                (await _characterService.GetAllAsync()).Any(c => c.Name == nickname);

            if (!isTrustedRefresh && !isColdStartMatch)
            {
                return;
            }

            CurrentEntityId = Convert.ToInt32(entityIdValue);
            CurrentCharacterName = nickname;
        }
        catch (Exception ex)
        {
            OnError?.Invoke(this, ex);
        }
    }

    private static bool TryToUshort(object value, out ushort result)
    {
        var numeric = Convert.ToInt64(value);
        if (numeric is >= ushort.MinValue and <= ushort.MaxValue)
        {
            result = (ushort)numeric;
            return true;
        }

        result = 0;
        return false;
    }
}
