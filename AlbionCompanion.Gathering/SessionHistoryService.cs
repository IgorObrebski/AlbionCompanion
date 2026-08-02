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

    public async Task<PagedResult<SessionSummary>> GetCompletedSessionsAsync(SessionQuery query)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();

        var filtered = dbContext.GatheringSessions.Where(s => s.EndTime != null);

        if (!string.IsNullOrWhiteSpace(query.LocationFilter))
        {
            filtered = filtered.Where(s => EF.Functions.Like(s.StartLocation, $"%{query.LocationFilter}%"));
        }

        var totalCount = await filtered.CountAsync();

        // Items has no dedicated column - it's a per-session sum over GatheredItems, so ordering
        // by it (unlike the other three columns) needs the same subquery expression EF translates
        // for the projection below, not a plain column reference.
        var sorted = query.SortBy switch
        {
            SessionSortColumn.Location => query.SortDescending
                ? filtered.OrderByDescending(s => s.StartLocation)
                : filtered.OrderBy(s => s.StartLocation),
            SessionSortColumn.Fame => query.SortDescending
                ? filtered.OrderByDescending(s => s.TotalFameEarned)
                : filtered.OrderBy(s => s.TotalFameEarned),
            SessionSortColumn.Items => query.SortDescending
                ? filtered.OrderByDescending(s => s.GatheredItems.Sum(i => (int?)i.Amount) ?? 0)
                : filtered.OrderBy(s => s.GatheredItems.Sum(i => (int?)i.Amount) ?? 0),
            _ => query.SortDescending
                ? filtered.OrderByDescending(s => s.StartTime)
                : filtered.OrderBy(s => s.StartTime),
        };

        var page = Math.Max(1, query.Page);
        var pageSize = Math.Max(1, query.PageSize);

        var items = await sorted
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(s => new SessionSummary(
                s.Id,
                s.StartTime,
                s.EndTime!.Value,
                s.StartLocation,
                s.TotalFameEarned,
                s.GatheredItems.Sum(i => (int?)i.Amount) ?? 0))
            .ToListAsync();

        return new PagedResult<SessionSummary>(items, totalCount, page, pageSize);
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
