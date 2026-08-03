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

        if (query.CharacterId is { } characterId)
        {
            filtered = filtered.Where(s => s.CharacterId == characterId);
        }

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

        // Only the current page's worth of sessions gets its Location set (needs every distinct
        // GatheredItem/FameLog location, which doesn't translate cleanly to SQL as a Distinct/Union
        // inside a Select projection) - fetched via Include and merged in memory, same as
        // GetSessionDetailAsync does for one session.
        var pageEntities = await sorted
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Include(s => s.GatheredItems)
            .Include(s => s.FameLogs)
            .Include(s => s.SilverLogs)
            .Include(s => s.Character)
            .ToListAsync();

        var items = pageEntities
            .Select(s => new SessionSummary(
                s.Id,
                s.StartTime,
                s.EndTime!.Value,
                s.StartLocation,
                LocationsVisited(
                    s.StartLocation,
                    s.GatheredItems.Select(i => i.Location),
                    s.FameLogs.Select(f => f.Location),
                    s.SilverLogs.Select(silver => silver.Location)),
                s.TotalFameEarned,
                s.GatheredItems.Sum(i => i.Amount),
                s.Character?.Name ?? "Unknown"))
            .ToList();

        return new PagedResult<SessionSummary>(items, totalCount, page, pageSize);
    }

    private static IReadOnlyList<string> LocationsVisited(
        string startLocation, IEnumerable<string> itemLocations, IEnumerable<string> fameLocations, IEnumerable<string> silverLocations)
    {
        var locations = itemLocations.Concat(fameLocations).Concat(silverLocations)
            .Where(l => !string.IsNullOrEmpty(l))
            .Distinct()
            .ToList();

        return locations.Count > 0 ? locations : new List<string> { startLocation };
    }

    public async Task<SessionDetail?> GetSessionDetailAsync(Guid sessionId)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();

        var session = await dbContext.GatheringSessions
            .Include(s => s.GatheredItems)
            .Include(s => s.FameLogs)
            .Include(s => s.SilverLogs)
            .SingleOrDefaultAsync(s => s.Id == sessionId && s.EndTime != null);

        if (session is null)
        {
            return null;
        }

        var itemTotals = session.GatheredItems
            .GroupBy(i => new { i.ItemId, i.Location })
            .Select(g => new ItemLocationTotal(g.Key.ItemId, g.Key.Location, g.Sum(i => i.Amount)))
            .ToList();

        var fameByLocation = session.FameLogs
            .GroupBy(f => f.Location)
            .Select(g => new LocationTotal(g.Key, g.Sum(f => f.Amount)))
            .ToList();

        var fameTimeline = session.FameLogs
            .OrderBy(f => f.Timestamp)
            .Select(f => new TimelineEvent(f.Timestamp, f.Amount))
            .ToList();

        var itemTimeline = session.GatheredItems
            .OrderBy(i => i.Timestamp)
            .Select(i => new TimelineEvent(i.Timestamp, i.Amount))
            .ToList();

        var silverByLocation = session.SilverLogs
            .GroupBy(silver => silver.Location)
            .Select(g => new LocationTotal(g.Key, g.Sum(silver => silver.Amount)))
            .ToList();

        var silverTimeline = session.SilverLogs
            .OrderBy(silver => silver.Timestamp)
            .Select(silver => new TimelineEvent(silver.Timestamp, silver.Amount))
            .ToList();

        return new SessionDetail(
            session.Id,
            session.StartTime,
            session.EndTime!.Value,
            session.StartLocation,
            session.TotalFameEarned,
            session.TotalSilverEarned,
            itemTotals,
            fameByLocation,
            silverByLocation,
            fameTimeline,
            itemTimeline,
            silverTimeline);
    }
}
