using System.Net.Http.Json;
using System.Text.RegularExpressions;
using AlbionCompanion.Core.Data;
using AlbionCompanion.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace AlbionCompanion.Gathering;

// Imports the item dictionary from ao-data/ao-bin-dumps' items.json on first run, per
// specs/albion-companion-context.md. This lets GatheredItem.ItemId values like "T4_ORE"
// (produced by GatheringEventRouter/HarvestableCategory) be resolved to a localized display
// name instead of staying an opaque game-internal id.
//
// The real items.json (checked 2026-07-16, ~12k entries) does NOT have explicit "Tier" or
// "ShopCategory" fields as originally assumed in the spec - only LocalizedNames and UniqueName.
// Tier and ItemGroup are derived from the UniqueName's "T{tier}_{REST}" convention instead
// (e.g. "T4_ORE" -> tier=4, group="ORE"); items that don't follow it (most equipment, e.g.
// "MAIN_SWORD") get Tier=0 and ItemGroup=the full UniqueName.
public class ItemDictionaryService : IItemDictionaryService
{
    private const string ItemsJsonUrl = "https://raw.githubusercontent.com/ao-data/ao-bin-dumps/master/formatted/items.json";

    private static readonly Regex TierPrefixPattern = new(@"^T(\d+)_(.+)$", RegexOptions.Compiled);

    private readonly AppDbContext _dbContext;
    private readonly HttpClient _httpClient;

    public ItemDictionaryService(AppDbContext dbContext, HttpClient httpClient)
    {
        _dbContext = dbContext;
        _httpClient = httpClient;
    }

    public async Task SeedFromJsonAsync()
    {
        if (await _dbContext.ItemDictionaries.AnyAsync())
        {
            return;
        }

        var entries = await _httpClient.GetFromJsonAsync<List<ItemJsonEntry>>(ItemsJsonUrl) ?? new List<ItemJsonEntry>();

        foreach (var entry in entries)
        {
            if (string.IsNullOrEmpty(entry.UniqueName))
            {
                continue;
            }

            var (tier, group) = ParseTierAndGroup(entry.UniqueName);

            _dbContext.ItemDictionaries.Add(new ItemDictionary
            {
                UniqueName = entry.UniqueName,
                DisplayNamePL = entry.LocalizedNames?.GetValueOrDefault("PL-PL") ?? entry.UniqueName,
                DisplayNameEN = entry.LocalizedNames?.GetValueOrDefault("EN-US") ?? entry.UniqueName,
                Tier = tier,
                ItemGroup = group,
            });
        }

        await _dbContext.SaveChangesAsync();
    }

    public Task<ItemDictionary?> GetItemByIdAsync(string id) =>
        _dbContext.ItemDictionaries.FirstOrDefaultAsync(item => item.UniqueName == id);

    public async Task<IEnumerable<ItemDictionary>> SearchItemsAsync(string query)
    {
        var pattern = $"%{query.Trim()}%";

        return await _dbContext.ItemDictionaries
            .Where(item =>
                EF.Functions.Like(item.DisplayNamePL, pattern) ||
                EF.Functions.Like(item.DisplayNameEN, pattern) ||
                EF.Functions.Like(item.UniqueName, pattern))
            .ToListAsync();
    }

    private static (int Tier, string ItemGroup) ParseTierAndGroup(string uniqueName)
    {
        var match = TierPrefixPattern.Match(uniqueName);
        return match.Success
            ? (int.Parse(match.Groups[1].Value), match.Groups[2].Value)
            : (0, uniqueName);
    }

    private sealed class ItemJsonEntry
    {
        public string? UniqueName { get; set; }
        public Dictionary<string, string>? LocalizedNames { get; set; }
    }
}
