using AlbionCompanion.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace AlbionCompanion.Gathering;

public class SessionHistoryService : ISessionHistoryService
{
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;

    public SessionHistoryService(IDbContextFactory<AppDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory;
    }

    public async Task<IReadOnlyList<SessionSummary>> GetCompletedSessionsAsync()
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();

        return await dbContext.GatheringSessions
            .Where(s => s.EndTime != null)
            .OrderByDescending(s => s.StartTime)
            .Select(s => new SessionSummary(
                s.Id,
                s.StartTime,
                s.EndTime!.Value,
                s.StartLocation,
                s.TotalFameEarned,
                s.GatheredItems.Sum(i => (int?)i.Amount) ?? 0))
            .ToListAsync();
    }

    public async Task<SessionDetail?> GetSessionDetailAsync(Guid sessionId)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();

        var session = await dbContext.GatheringSessions
            .Include(s => s.GatheredItems)
            .SingleOrDefaultAsync(s => s.Id == sessionId && s.EndTime != null);

        if (session is null)
        {
            return null;
        }

        var itemTotals = session.GatheredItems
            .GroupBy(i => i.ItemId)
            .ToDictionary(g => g.Key, g => g.Sum(i => i.Amount));

        return new SessionDetail(
            session.Id,
            session.StartTime,
            session.EndTime!.Value,
            session.StartLocation,
            session.TotalFameEarned,
            itemTotals);
    }
}
