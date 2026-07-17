namespace AlbionCompanion.Gathering;

public interface ISessionHistoryService
{
    Task<IReadOnlyList<SessionSummary>> GetCompletedSessionsAsync();
    Task<SessionDetail?> GetSessionDetailAsync(Guid sessionId);
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
