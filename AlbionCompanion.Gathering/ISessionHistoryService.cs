namespace AlbionCompanion.Gathering;

public interface ISessionHistoryService
{
    Task<PagedResult<SessionSummary>> GetCompletedSessionsAsync(SessionQuery query);
    Task<SessionDetail?> GetSessionDetailAsync(Guid sessionId);
}

public enum SessionSortColumn
{
    StartTime,
    Location,
    Fame,
    Items,
}

public record SessionQuery(
    int Page = 1,
    int PageSize = 20,
    string? LocationFilter = null,
    SessionSortColumn SortBy = SessionSortColumn.StartTime,
    bool SortDescending = true);

public record PagedResult<T>(IReadOnlyList<T> Items, int TotalCount, int Page, int PageSize)
{
    public int TotalPages => PageSize <= 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);
}

public record SessionSummary(
    Guid Id,
    DateTime StartTime,
    DateTime EndTime,
    string StartLocation,
    int TotalFameEarned,
    int TotalItemsCollected);

public record SessionDetail(
    Guid Id,
    DateTime StartTime,
    DateTime EndTime,
    string StartLocation,
    int TotalFameEarned,
    IReadOnlyDictionary<string, int> ItemTotals);
