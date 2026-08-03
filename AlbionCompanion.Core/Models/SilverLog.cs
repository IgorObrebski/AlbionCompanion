using System.ComponentModel.DataAnnotations;

namespace AlbionCompanion.Core.Models;

public class SilverLog
{
    [Key]
    public int Id { get; set; }
    public Guid SessionId { get; set; }
    public GatheringSession? Session { get; set; }
    public int Amount { get; set; }
    // Session.CurrentLocation at the moment this was earned - same reasoning as
    // GatheredItem.Location/FameLog.Location.
    public string Location { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
}
