using System.ComponentModel.DataAnnotations;

namespace AlbionCompanion.Core.Models;

public class FlipLog
{
    [Key]
    public int Id { get; set; }
    public string ItemId { get; set; } = string.Empty;
    public string OrderType { get; set; } = string.Empty; // "Buy" lub "Sell"
    public int PricePerItem { get; set; }
    public int Amount { get; set; }
    public int TaxPaid { get; set; }
    public string Location { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public string Source { get; set; } = string.Empty; // "LiveSniffer" lub "MailboxSync"
}
