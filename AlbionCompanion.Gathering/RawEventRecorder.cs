using System.Text.Json;
using AlbionCompanion.Core.Data;
using AlbionCompanion.Core.Models;
using AlbionCompanion.Sniffer.Protocol16;
using Microsoft.EntityFrameworkCore;

namespace AlbionCompanion.Gathering;

// Records every Photon event verbatim, independent of GatheringEventRouter's interpretation
// logic. No event-code filtering, no actor filtering: the point is to never lose a field to a
// bad interpretation decision made today. See
// docs/superpowers/specs/2026-07-17-raw-gathering-event-log-design.md.
//
// Dispatch is fire-and-forget (photonParser.OnEventReceived += (_, e) => _ = HandleEventAsync(e)),
// so overlapping events are expected and normal under live capture. Each event gets its own
// freshly-created AppDbContext via IDbContextFactory, rather than sharing the scoped instance
// injected into GatheringSessionService/GatheringEventRouter - EF Core forbids concurrent
// operations on a single DbContext instance, and without this isolation two events arriving
// close together would throw "A second operation was started on this context instance before a
// previous operation completed." The active-session lookup still goes through the injected
// IGatheringSessionService (and therefore its own shared context) because that is a read against
// a service not expected to be called concurrently with itself; only the write below needed to
// move off the shared instance.
public class RawEventRecorder : IRawEventRecorder
{
    private const byte SemanticEventCodeParameterKey = 252;

    private readonly IGatheringSessionService _sessionService;
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;

    public event EventHandler<Exception>? OnRecordFailure;

    public RawEventRecorder(IPhotonParser photonParser, IGatheringSessionService sessionService, IDbContextFactory<AppDbContext> dbContextFactory)
    {
        _sessionService = sessionService;
        _dbContextFactory = dbContextFactory;
        photonParser.OnEventReceived += (_, e) => _ = HandleEventAsync(e);
    }

    internal async Task HandleEventAsync(PhotonEvent photonEvent)
    {
        try
        {
            var session = await _sessionService.GetActiveSessionAsync();

            await using var dbContext = await _dbContextFactory.CreateDbContextAsync();

            dbContext.RawGatheringEvents.Add(new RawGatheringEvent
            {
                SessionId = session?.Id,
                PhotonCode = photonEvent.Code,
                SemanticEventCode = TryGetSemanticEventCode(photonEvent),
                ParametersJson = JsonSerializer.Serialize(photonEvent.Parameters.ToDictionary(p => p.Key.ToString(), p => p.Value)),
                Timestamp = DateTime.UtcNow,
            });

            await dbContext.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            OnRecordFailure?.Invoke(this, ex);
        }
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
