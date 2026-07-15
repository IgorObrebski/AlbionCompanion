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
}
