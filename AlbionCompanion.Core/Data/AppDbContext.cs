using AlbionCompanion.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace AlbionCompanion.Core.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<GatheringSession> GatheringSessions => Set<GatheringSession>();
    public DbSet<GatheredItem> GatheredItems => Set<GatheredItem>();
    public DbSet<FameLog> FameLogs => Set<FameLog>();
    public DbSet<FlipLog> FlipLogs => Set<FlipLog>();
    public DbSet<ItemDictionary> ItemDictionaries => Set<ItemDictionary>();
    public DbSet<PriceCache> PriceCaches => Set<PriceCache>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PriceCache>().HasKey(priceCache => new { priceCache.ItemId, priceCache.Location });
    }
}
