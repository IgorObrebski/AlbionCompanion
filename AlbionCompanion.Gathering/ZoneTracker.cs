using AlbionCompanion.Sniffer.Protocol16;

namespace AlbionCompanion.Gathering;

// Drives gathering-session start/end from zone changes. Confirmed via live capture on
// 2026-07-16: the outer PhotonResponse.OperationCode is always 1 (a generic wrapper, same
// pattern as PhotonEvent.Code); the real sub-operation lives in parameter 253, and the
// "you joined a zone" response (253 == 2) carries the new zone's numeric id in parameter 8.
//
// Originally this tracked "returned to the zone id first seen at app start" as a home-zone
// heuristic, but that broke as soon as the player visited their city's bank/market: those are
// separate zoneIds from the main city, so they looked like "left home" and spuriously started a
// session. IZoneCatalog (ao-bin-dumps zones.json) now classifies each zone as city/safe-area vs.
// open world directly, which handles bank/market/portals correctly without needing to remember
// where the player started.
public class ZoneTracker
{
    private const byte ZoneResponseSubCodeKey = 253;
    private const byte ZoneResponseSubCode = 2;
    private const byte CurrentZoneIdParameterKey = 8;

    private readonly IGatheringSessionService _sessionService;
    private readonly IZoneCatalog _zoneCatalog;

    public ZoneTracker(IPhotonParser photonParser, IGatheringSessionService sessionService, IZoneCatalog zoneCatalog)
    {
        _sessionService = sessionService;
        _zoneCatalog = zoneCatalog;
        photonParser.OnResponseReceived += (_, response) => _ = HandleResponseAsync(response);
    }

    internal async Task HandleResponseAsync(PhotonResponse response)
    {
        if (!response.Parameters.TryGetValue(ZoneResponseSubCodeKey, out var subCode) ||
            Convert.ToInt32(subCode) != ZoneResponseSubCode)
        {
            return;
        }

        if (!response.Parameters.TryGetValue(CurrentZoneIdParameterKey, out var zoneIdValue) || zoneIdValue is null)
        {
            return;
        }

        var zoneId = Convert.ToInt32(zoneIdValue);

        if (await _zoneCatalog.IsCityOrSafeAreaAsync(zoneId))
        {
            await _sessionService.EndSessionAsync();
            return;
        }

        var zone = await _zoneCatalog.GetZoneAsync(zoneId);
        await _sessionService.StartSessionAsync(zone?.Name ?? zoneId.ToString());
    }
}
