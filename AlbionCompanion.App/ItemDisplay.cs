using System.Text.RegularExpressions;

namespace AlbionCompanion.App;

// Maps a GatheredItems.ItemId (usually "T{tier}_{CATEGORY}", optionally "_LEVEL{n}@{n}" for
// enchanted resources - see GatheringEventRouter.ResolveItemId - occasionally a bare numeric
// fallback when tier resolution failed) to display metadata for the UI: which category icon to
// show, which tier accent color to badge it with, and the enchantment badge (color + name).
// Purely cosmetic - falls back to a generic look for anything that doesn't parse, same spirit as
// the router's own "approximate id beats dropping the swing" fallback.
public static class ItemDisplay
{
    private static readonly Regex EnchantmentSuffixPattern = new(@"_LEVEL\d+@(\d+)$", RegexOptions.Compiled);

    public static (int? Tier, string Category, int EnchantmentLevel) Parse(string itemId)
    {
        var parts = itemId.Split('_', 2);
        if (parts.Length != 2 || parts[0].Length <= 1 || parts[0][0] != 'T' || !int.TryParse(parts[0][1..], out var tier))
        {
            return (null, itemId, 0);
        }

        var enchantMatch = EnchantmentSuffixPattern.Match(parts[1]);
        if (!enchantMatch.Success)
        {
            return (tier, parts[1], 0);
        }

        var category = parts[1][..enchantMatch.Index];
        return (tier, category, int.Parse(enchantMatch.Groups[1].Value));
    }

    public static string TierCssClass(int? tier) => tier switch
    {
        1 => "ac-tier-1",
        2 => "ac-tier-2",
        3 => "ac-tier-3",
        4 => "ac-tier-4",
        5 => "ac-tier-5",
        6 => "ac-tier-6",
        7 => "ac-tier-7",
        8 => "ac-tier-8",
        _ => "ac-tier-unknown",
    };

    // Names and colors confirmed against the Albion Online Wiki (wiki.albiononline.com/wiki/Enchanting):
    // .1 green "Uncommon", .2 blue "Rare", .3 purple "Exceptional", .4 gold "Pristine". Reuses the
    // same green/blue/purple/gold already established for T3/T4/T5/T7 tier badges for consistency.
    public static string? EnchantmentLabel(int level) => level switch
    {
        1 => "Uncommon",
        2 => "Rare",
        3 => "Exceptional",
        4 => "Pristine",
        _ => null,
    };

    public static string EnchantmentCssClass(int level) => level switch
    {
        1 => "ac-enchant-1",
        2 => "ac-enchant-2",
        3 => "ac-enchant-3",
        4 => "ac-enchant-4",
        _ => "ac-enchant-0",
    };

    public static string[] CategoryIconPaths(string category) => category.ToUpperInvariant() switch
    {
        "ORE" => ["M6 3h12l4 6-10 13L2 9Z", "M11 3 8 9l4 13 4-13-3-6", "M2 9h20"],
        "FIBER" => ["M11 20A7 7 0 0 1 9.8 6.1C15.5 5 17 4.48 19 2c1 2 2 4.18 2 8 0 5.5-4.78 10-10 10Z", "M2 21c0-3 1.85-5.36 5.08-6C9.5 14.52 12 13 13 12"],
        "ROCK" => ["m8 3 4 8 5-5 5 15H2L8 3z"],
        "HIDE" => ["M9 10a5 5 0 0 1 5 5v3.5a3.5 3.5 0 0 1-6.84 1.045Q6.52 17.48 4.46 16.84A3.5 3.5 0 0 1 5.5 10Z"],
        "WOOD" => ["m17 14 3 3.3a1 1 0 0 1-.7 1.7H4.7a1 1 0 0 1-.7-1.7L7 14h-.3a1 1 0 0 1-.7-1.7L9 9h-.2A1 1 0 0 1 8 7.3L12 3l4 4.3a1 1 0 0 1-.8 1.7H15l3 3.3a1 1 0 0 1-.7 1.7H17Z", "M12 22v-3"],
        _ => ["M21 8a2 2 0 0 0-1-1.73l-7-4a2 2 0 0 0-2 0l-7 4A2 2 0 0 0 3 8v8a2 2 0 0 0 1 1.73l7 4a2 2 0 0 0 2 0l7-4A2 2 0 0 0 21 16Z", "M3.3 7 12 12l8.7-5", "M12 22V12"],
    };
}
