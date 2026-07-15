using System.ComponentModel.DataAnnotations;

namespace AlbionCompanion.Core.Models;

public class FameLog
{
    [Key]
    public int Id { get; set; }
    public Guid SessionId { get; set; }
    public GatheringSession? Session { get; set; }
    public string FameType { get; set; } = string.Empty; // "Gathering", "MobKill"
    public int Amount { get; set; }
    public DateTime Timestamp { get; set; }
}
