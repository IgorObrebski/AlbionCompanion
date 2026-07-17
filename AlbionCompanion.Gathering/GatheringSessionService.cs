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

    public event EventHandler<GatheringSession>? OnSessionStarted;
    public event EventHandler<GatheringSession>? OnSessionEnded;
    public event EventHandler<GatheredItem>? OnItemAdded;
    public event EventHandler<FameLog>? OnFameAdded;

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

        var session = new GatheringSession
        {
            StartTime = DateTime.UtcNow,
            StartLocation = location,
        };
        _dbContext.GatheringSessions.Add(session);

        await _dbContext.SaveChangesAsync();
        OnSessionStarted?.Invoke(this, session);
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
            await _dbContext.SaveChangesAsync();
            return;
        }

        session.EndTime = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync();
        OnSessionEnded?.Invoke(this, session);
    }

    public async Task AddItemAsync(string itemId, int amount)
    {
        var session = await GetActiveSessionAsync();
        if (session is null)
        {
            // No open wilderness session to attribute this to (e.g. picked up in town) - ignore.
            return;
        }

        var item = new GatheredItem
        {
            SessionId = session.Id,
            ItemId = itemId,
            Amount = amount,
            Timestamp = DateTime.UtcNow,
        };
        _dbContext.GatheredItems.Add(item);

        await _dbContext.SaveChangesAsync();
        OnItemAdded?.Invoke(this, item);
    }

    public async Task AddFameAsync(string fameType, int amount)
    {
        var session = await GetActiveSessionAsync();
        if (session is null)
        {
            return;
        }

        var fameLog = new FameLog
        {
            SessionId = session.Id,
            FameType = fameType,
            Amount = amount,
            Timestamp = DateTime.UtcNow,
        };
        _dbContext.FameLogs.Add(fameLog);
        session.TotalFameEarned += amount;

        await _dbContext.SaveChangesAsync();
        OnFameAdded?.Invoke(this, fameLog);
    }
}
