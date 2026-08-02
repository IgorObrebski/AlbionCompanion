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
    // Session.CurrentLocation at the moment this was gathered - a session can roam through
    // multiple zones (see the 2026-08-02 roaming fix) without ending, so this is needed to answer
    // "what did I get, broken down by where" instead of only "what did I get, total".
    public string Location { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
}
