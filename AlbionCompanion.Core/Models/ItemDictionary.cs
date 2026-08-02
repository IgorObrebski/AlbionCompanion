using System.ComponentModel.DataAnnotations;

namespace AlbionCompanion.Core.Models;

public class ItemDictionary
{
    [Key]
    public string UniqueName { get; set; } = string.Empty; // np. "T4_ORE"
    public string DisplayNamePL { get; set; } = string.Empty;
    public string DisplayNameEN { get; set; } = string.Empty;
    public int Tier { get; set; }
    public string ItemGroup { get; set; } = string.Empty;

    // ao-bin-dumps items.json's own numeric item id, unique per UniqueName (confirmed 2026-08-02:
    // no missing or duplicate values across ~12k entries). This is the same value HarvestFinished
    // (Photon event code 61) reports directly in its own parameter 4 - looking a swing's item up
    // by Index instead of composing "T{tier}_{CATEGORY}" from separately-tracked node state gives
    // the exact item (including enchantment level) with no dependency on ever having seen that
    // node's own spawn broadcast.
    public int Index { get; set; }
}
