using System.Text.Json;
using AlbionCompanion.Core.Data;
using AlbionCompanion.Core.Models;
using AlbionCompanion.Sniffer.Protocol16;

namespace AlbionCompanion.Gathering;

// Records every Photon event verbatim, independent of GatheringEventRouter's interpretation
// logic. No event-code filtering, no actor filtering: the point is to never lose a field to a
// bad interpretation decision made today. See
// docs/superpowers/specs/2026-07-17-raw-gathering-event-log-design.md.
public class RawEventRecorder : IRawEventRecorder
{
    private const byte SemanticEventCodeParameterKey = 252;

    private readonly IGatheringSessionService _sessionService;
    private readonly AppDbContext _dbContext;

    public RawEventRecorder(IPhotonParser photonParser, IGatheringSessionService sessionService, AppDbContext dbContext)
    {
        _sessionService = sessionService;
        _dbContext = dbContext;
        photonParser.OnEventReceived += (_, e) => _ = HandleEventAsync(e);
    }

    internal async Task HandleEventAsync(PhotonEvent photonEvent)
    {
        var session = await _sessionService.GetActiveSessionAsync();

        _dbContext.RawGatheringEvents.Add(new RawGatheringEvent
        {
            SessionId = session?.Id,
            PhotonCode = photonEvent.Code,
            SemanticEventCode = TryGetSemanticEventCode(photonEvent),
            ParametersJson = JsonSerializer.Serialize(photonEvent.Parameters.ToDictionary(p => p.Key.ToString(), p => p.Value)),
            Timestamp = DateTime.UtcNow,
        });

        await _dbContext.SaveChangesAsync();
    }

    private static byte? TryGetSemanticEventCode(PhotonEvent photonEvent)
    {
        if (!photonEvent.Parameters.TryGetValue(SemanticEventCodeParameterKey, out var value) || value is null)
        {
            return null;
        }

        var numeric = Convert.ToInt64(value);
        return numeric is >= byte.MinValue and <= byte.MaxValue ? (byte)numeric : null;
    }
}
