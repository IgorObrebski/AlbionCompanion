using System.ComponentModel.DataAnnotations;

namespace AlbionCompanion.Core.Models;

public class GatheredItem
{
    [Key]
    public int Id { get; set; }
    public Guid SessionId { get; set; }
    public GatheringSession? Session { get; set; }
    public string ItemId { get; set; } = string.Empty;
    public int Amount { get; set; }
    public DateTime Timestamp { get; set; }
}
