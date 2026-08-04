using AlbionCompanion.Core.Data;
using AlbionCompanion.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace AlbionCompanion.Gathering;

// Mirrors ItemDictionaryService's shape (IDbContextFactory, registered as a Singleton) - see
// AppHostBuilder.cs. LocalPlayerTracker (also a Singleton) depends on this for its cold-start
// character-name match, so this can't be Scoped.
public class CharacterService : ICharacterService
{
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;

    public event EventHandler? CharactersChanged;

    public CharacterService(IDbContextFactory<AppDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory;
    }

    public async Task<IReadOnlyList<Character>> GetAllAsync()
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        return await dbContext.Characters.OrderBy(c => c.Name).ToListAsync();
    }

    public async Task<Character> AddAsync(string name)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();

        var character = new Character { Name = name, CreatedAt = DateTime.UtcNow };
        dbContext.Characters.Add(character);
        await dbContext.SaveChangesAsync();
        NotifyCharactersChanged();

        return character;
    }

    public async Task DeleteAsync(Guid id)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();

        var character = await dbContext.Characters.FindAsync(id);
        if (character is null)
        {
            return;
        }

        dbContext.Characters.Remove(character);
        await dbContext.SaveChangesAsync();
        NotifyCharactersChanged();
    }

    public async Task RenameAsync(Guid id, string newName)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();

        var character = await dbContext.Characters.FindAsync(id);
        if (character is null)
        {
            return;
        }

        character.Name = newName;
        await dbContext.SaveChangesAsync();
        NotifyCharactersChanged();
    }

    public async Task<IReadOnlyList<CharacterOverview>> GetAllOverviewsAsync()
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();

        var characters = await dbContext.Characters.OrderBy(c => c.Name).ToListAsync();
        var activeCharacterId = await GetActiveCharacterIdAsync(dbContext);

        // A handful of characters at most (realistically 1-10) - a per-character round trip in
        // a loop is simpler to read and test than one mega-query, and this isn't a hot path
        // (called once per hub-page load).
        var overviews = new List<CharacterOverview>();
        foreach (var character in characters)
        {
            overviews.Add(await BuildOverviewAsync(dbContext, character, activeCharacterId));
        }

        return overviews;
    }

    public async Task<CharacterOverview?> GetOverviewAsync(Guid characterId)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();

        var character = await dbContext.Characters.FindAsync(characterId);
        if (character is null)
        {
            return null;
        }

        var activeCharacterId = await GetActiveCharacterIdAsync(dbContext);
        return await BuildOverviewAsync(dbContext, character, activeCharacterId);
    }

    private static async Task<Guid?> GetActiveCharacterIdAsync(AppDbContext dbContext) =>
        await dbContext.GatheringSessions
            .Where(session => session.EndTime == null)
            .Select(session => session.CharacterId)
            .FirstOrDefaultAsync();

    private static async Task<CharacterOverview> BuildOverviewAsync(AppDbContext dbContext, Character character, Guid? activeCharacterId)
    {
        var sessions = dbContext.GatheringSessions.Where(session => session.CharacterId == character.Id);

        var totalFame = await sessions.SumAsync(session => (int?)session.TotalFameEarned) ?? 0;
        var totalSilver = await sessions.SumAsync(session => (int?)session.TotalSilverEarned) ?? 0;
        var totalItems = await dbContext.GatheredItems
            .Where(item => item.Session!.CharacterId == character.Id)
            .SumAsync(item => (int?)item.Amount) ?? 0;
        var lastActive = await sessions.MaxAsync(session => (DateTime?)session.StartTime);

        return new CharacterOverview(
            character.Id,
            character.Name,
            totalFame,
            totalSilver,
            totalItems,
            lastActive,
            character.Id == activeCharacterId);
    }

    public void NotifyCharactersChanged() => CharactersChanged?.Invoke(this, EventArgs.Empty);
}
