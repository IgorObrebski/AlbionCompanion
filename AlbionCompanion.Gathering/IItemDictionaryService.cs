using AlbionCompanion.Core.Models;

namespace AlbionCompanion.Gathering;

public interface IItemDictionaryService
{
    Task<IEnumerable<ItemDictionary>> SearchItemsAsync(string query);
    Task<ItemDictionary?> GetItemByIdAsync(string id);
    Task SeedFromJsonAsync();
}
