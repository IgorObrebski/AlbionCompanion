using AlbionCompanion.Core.Data;
using AlbionCompanion.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace AlbionCompanion.Gathering;

public class GatheringSessionService : IGatheringSessionService
{
    private readonly AppDbContext _dbContext;
    private readonly ILocalPlayerTracker _localPlayerTracker;
    private readonly ICharacterService _characterService;

    public GatheringSessionService(AppDbContext dbContext, ILocalPlayerTracker localPlayerTracker, ICharacterService characterService)
    {
        _dbContext = dbContext;
        _localPlayerTracker = localPlayerTracker;
        _characterService = characterService;
    }

    public event EventHandler<GatheringSession>? OnSessionStarted;
    public event EventHandler<GatheringSession>? OnSessionEnded;
    public event EventHandler<GatheringSession>? OnLocationChanged;
    public event EventHandler<GatheredItem>? OnItemAdded;
    public event EventHandler<FameLog>? OnFameAdded;
    public event EventHandler<SilverLog>? OnSilverAdded;

    // A session with no gathering activity logged for this long is almost certainly orphaned -
    // ZoneTracker only ever ends a session on a return-to-city zone transition, so closing the
    // game/app while still in open world leaves EndTime null forever. Confirmed live 2026-08-04:
    // a session from the previous day got silently "resumed" on the next app launch instead of a
    // new one starting, with a permanently-null CharacterId (frozen at whatever it resolved to
    // when the orphaned session started).
    private static readonly TimeSpan StaleSessionThreshold = TimeSpan.FromHours(1);

    public async Task<GatheringSession?> GetActiveSessionAsync()
    {
        var session = await _dbContext.GatheringSessions.SingleOrDefaultAsync(s => s.EndTime == null);
        if (session is null)
        {
            return null;
        }

        var lastActivity = await GetLastActivityAsync(session.Id) ?? session.StartTime;
        if (DateTime.UtcNow - lastActivity > StaleSessionThreshold)
        {
            await CloseSessionAsync(session);
            return null;
        }

        return session;
    }

    private async Task<DateTime?> GetLastActivityAsync(Guid sessionId)
    {
        var latestItem = await _dbContext.GatheredItems
            .Where(item => item.SessionId == sessionId)
            .MaxAsync(item => (DateTime?)item.Timestamp);
        var latestFame = await _dbContext.FameLogs
            .Where(fame => fame.SessionId == sessionId)
            .MaxAsync(fame => (DateTime?)fame.Timestamp);
        var latestSilver = await _dbContext.SilverLogs
            .Where(silver => silver.SessionId == sessionId)
            .MaxAsync(silver => (DateTime?)silver.Timestamp);

        DateTime? latest = null;
        foreach (var candidate in new[] { latestItem, latestFame, latestSilver })
        {
            if (candidate is { } value && (latest is null || value > latest))
            {
                latest = value;
            }
        }

        return latest;
    }

    public async Task<ActiveSessionSnapshot?> GetActiveSessionSnapshotAsync()
    {
        var session = await GetActiveSessionAsync();
        if (session is null)
        {
            return null;
        }

        var itemTotals = await _dbContext.GatheredItems
            .Where(item => item.SessionId == session.Id)
            .GroupBy(item => new { item.ItemId, item.Location })
            .Select(g => new ItemLocationTotal(g.Key.ItemId, g.Key.Location, g.Sum(item => item.Amount)))
            .ToListAsync();

        var fameByLocation = await _dbContext.FameLogs
            .Where(fame => fame.SessionId == session.Id)
            .GroupBy(fame => fame.Location)
            .Select(g => new LocationTotal(g.Key, g.Sum(fame => fame.Amount)))
            .ToListAsync();

        var silverByLocation = await _dbContext.SilverLogs
            .Where(silver => silver.SessionId == session.Id)
            .GroupBy(silver => silver.Location)
            .Select(g => new LocationTotal(g.Key, g.Sum(silver => silver.Amount)))
            .ToListAsync();

        return new ActiveSessionSnapshot(
            session.CurrentLocation,
            session.CharacterId,
            session.TotalFameEarned,
            session.TotalSilverEarned,
            itemTotals,
            fameByLocation,
            silverByLocation);
    }

    public async Task StartSessionAsync(string location)
    {
        // Invariant: at most one open (EndTime == null) session at a time. A wilderness session
        // can roam through many open-world zones without ending (only a return to a city/safe
        // area ends it - see EndSessionAsync/ZoneTracker), so a zone change while a session is
        // already active isn't a duplicate to ignore - it's the player moving. Update
        // CurrentLocation to reflect that instead of silently no-op'ing, or the session's location
        // would stay frozen at wherever it started (confirmed via live capture on 2026-07-18: a
        // session that started in one zone and roamed to another still showed the first zone as
        // "current" indefinitely).
        if (await GetActiveSessionAsync() is { } activeSession)
        {
            if (activeSession.CurrentLocation != location)
            {
                activeSession.CurrentLocation = location;
                await _dbContext.SaveChangesAsync();
                OnLocationChanged?.Invoke(this, activeSession);
            }

            return;
        }

        var session = new GatheringSession
        {
            StartTime = DateTime.UtcNow,
            StartLocation = location,
            CurrentLocation = location,
            CharacterId = await ResolveCharacterIdAsync(),
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

        await CloseSessionAsync(session);
    }

    private async Task CloseSessionAsync(GatheringSession session)
    {
        // An empty run (nothing gathered before returning to town) isn't worth keeping.
        // Queried directly by SessionId rather than via the session's navigation collections,
        // which GetActiveSessionAsync doesn't Include and so would always read as empty.
        var hasAnyActivity =
            await _dbContext.GatheredItems.AnyAsync(item => item.SessionId == session.Id) ||
            await _dbContext.FameLogs.AnyAsync(fame => fame.SessionId == session.Id) ||
            await _dbContext.SilverLogs.AnyAsync(silver => silver.SessionId == session.Id);

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
            Location = session.CurrentLocation,
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
            Location = session.CurrentLocation,
            Timestamp = DateTime.UtcNow,
        };
        _dbContext.FameLogs.Add(fameLog);
        session.TotalFameEarned += amount;

        await _dbContext.SaveChangesAsync();
        OnFameAdded?.Invoke(this, fameLog);
    }

    public async Task AddSilverAsync(int amount)
    {
        var session = await GetActiveSessionAsync();
        if (session is null)
        {
            return;
        }

        var silverLog = new SilverLog
        {
            SessionId = session.Id,
            Amount = amount,
            Location = session.CurrentLocation,
            Timestamp = DateTime.UtcNow,
        };
        _dbContext.SilverLogs.Add(silverLog);
        session.TotalSilverEarned += amount;

        await _dbContext.SaveChangesAsync();
        OnSilverAdded?.Invoke(this, silverLog);
    }

    private async Task<Guid?> ResolveCharacterIdAsync()
    {
        if (_localPlayerTracker.CurrentCharacterName is not { } name)
        {
            return null;
        }

        var characters = await _characterService.GetAllAsync();
        return characters.FirstOrDefault(c => c.Name == name)?.Id;
    }
}
