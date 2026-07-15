namespace AlbionCompanion.Core.Models;

public class PriceCache
{
    // Composite key: (ItemId, Location) - configured in AppDbContext.OnModelCreating
    public string ItemId { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public int SellPriceMin { get; set; }
    public int BuyPriceMax { get; set; }
    public DateTime LastUpdated { get; set; }
}
