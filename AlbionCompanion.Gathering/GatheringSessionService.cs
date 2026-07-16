using AlbionCompanion.Core.Data;
using AlbionCompanion.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace AlbionCompanion.Gathering;

public class GatheringSessionService : IGatheringSessionService
{
    private readonly AppDbContext _dbContext;

    public GatheringSessionService(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<GatheringSession?> GetActiveSessionAsync() =>
        await _dbContext.GatheringSessions.SingleOrDefaultAsync(session => session.EndTime == null);

    public async Task StartSessionAsync(string location)
    {
        // Invariant: at most one open (EndTime == null) session at a time - if one is already
        // active (e.g. a duplicate zone-change event, or resuming after a DC in the wilderness),
        // this is a no-op rather than starting a second concurrent session.
        if (await GetActiveSessionAsync() is not null)
        {
            return;
        }

        _dbContext.GatheringSessions.Add(new GatheringSession
        {
            StartTime = DateTime.UtcNow,
            StartLocation = location,
        });

        await _dbContext.SaveChangesAsync();
    }

    public async Task EndSessionAsync()
    {
        var session = await GetActiveSessionAsync();
        if (session is null)
        {
            return;
        }

        // An empty run (nothing gathered before returning to town) isn't worth keeping.
        // Queried directly by SessionId rather than via the session's navigation collections,
        // which GetActiveSessionAsync doesn't Include and so would always read as empty.
        var hasAnyActivity =
            await _dbContext.GatheredItems.AnyAsync(item => item.SessionId == session.Id) ||
            await _dbContext.FameLogs.AnyAsync(fame => fame.SessionId == session.Id);

        if (!hasAnyActivity)
        {
            _dbContext.GatheringSessions.Remove(session);
        }
        else
        {
            session.EndTime = DateTime.UtcNow;
        }

        await _dbContext.SaveChangesAsync();
    }

    public async Task AddItemAsync(string itemId, int amount)
    {
        var session = await GetActiveSessionAsync();
        if (session is null)
        {
            // No open wilderness session to attribute this to (e.g. picked up in town) - ignore.
            return;
        }

        _dbContext.GatheredItems.Add(new GatheredItem
        {
            SessionId = session.Id,
            ItemId = itemId,
            Amount = amount,
            Timestamp = DateTime.UtcNow,
        });

        await _dbContext.SaveChangesAsync();
    }

    public async Task AddFameAsync(string fameType, int amount)
    {
        var session = await GetActiveSessionAsync();
        if (session is null)
        {
            return;
        }

        _dbContext.FameLogs.Add(new FameLog
        {
            SessionId = session.Id,
            FameType = fameType,
            Amount = amount,
            Timestamp = DateTime.UtcNow,
        });
        session.TotalFameEarned += amount;

        await _dbContext.SaveChangesAsync();
    }
}
