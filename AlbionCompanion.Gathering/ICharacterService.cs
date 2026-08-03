using AlbionCompanion.Core.Models;

namespace AlbionCompanion.Gathering;

public interface ICharacterService
{
    Task<IReadOnlyList<Character>> GetAllAsync();
    Task<Character> AddAsync(string name);
    Task DeleteAsync(Guid id);
    Task<IReadOnlyList<CharacterOverview>> GetAllOverviewsAsync();
    Task<CharacterOverview?> GetOverviewAsync(Guid characterId);
}

// One character's aggregate stats across every session it's attached to - the character hub
// card and the character dashboard's summary cards both read this shape.
public record CharacterOverview(
    Guid Id,
    string Name,
    int TotalFameEarned,
    int TotalSilverEarned,
    int TotalItemsCollected,
    DateTime? LastActive,
    bool HasActiveSession);
