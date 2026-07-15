using System.ComponentModel.DataAnnotations;

namespace AlbionCompanion.Core.Models;

public class GatheringSession
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime StartTime { get; set; }
    public DateTime? EndTime { get; set; } // null = sesja aktywna lub DC
    public string StartLocation { get; set; } = string.Empty;
    public int TotalFameEarned { get; set; }
    public ICollection<GatheredItem> GatheredItems { get; set; } = new List<GatheredItem>();
    public ICollection<FameLog> FameLogs { get; set; } = new List<FameLog>();
}
