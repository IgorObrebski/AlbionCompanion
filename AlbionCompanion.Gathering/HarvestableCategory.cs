namespace AlbionCompanion.Gathering;

// Category ranges confirmed from Nouuu/Albion-Online-OpenRadar's
// web/scripts/data/HarvestablesDatabase.js (getResourceTypeFromTypeNumber) - a hardcoded,
// non-contiguous-width range table, not a simple modulo/division formula. Category is
// independent of tier: the same category code (e.g. 27) covers a resource at every tier
// (confirmed via live capture - code 27 appeared at both tier 4 and tier 5), which is why
// Iron (T4 Ore), Tin (T3 Ore) and Titanium (T5 Ore) all carry the same "ORE" category code.
public static class HarvestableCategory
{
    public static string? FromTypeCode(int typeCode) => typeCode switch
    {
        >= 0 and <= 5 => "WOOD",
        >= 6 and <= 10 => "ROCK",
        >= 11 and <= 15 => "FIBER",
        >= 16 and <= 22 => "HIDE",
        >= 23 and <= 27 => "ORE",
        // Per OpenRadar's docs/technical/HARVEST_EVENTS.md: this type-number field is
        // unreliable for "living" resources (critters) - a code outside every known range
        // most likely means that, not a new resource category.
        _ => null,
    };
}
