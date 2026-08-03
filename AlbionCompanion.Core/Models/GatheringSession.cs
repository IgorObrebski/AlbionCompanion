using System.ComponentModel.DataAnnotations;

namespace AlbionCompanion.Core.Models;

public class GatheringSession
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime StartTime { get; set; }
    public DateTime? EndTime { get; set; } // null = sesja aktywna lub DC
    public string StartLocation { get; set; } = string.Empty;
    // Updated on every subsequent open-world zone change during the same session - StartLocation
    // stays frozen at wherever the session began, but a wilderness session can roam through many
    // zones without ending (only a return to a city/safe area ends it), so it alone can't answer
    // "where is the player right now."
    public string CurrentLocation { get; set; } = string.Empty;
    public int TotalFameEarned { get; set; }
    public int TotalSilverEarned { get; set; }
    public ICollection<GatheredItem> GatheredItems { get; set; } = new List<GatheredItem>();
    public ICollection<FameLog> FameLogs { get; set; } = new List<FameLog>();
    public ICollection<SilverLog> SilverLogs { get; set; } = new List<SilverLog>();
}
