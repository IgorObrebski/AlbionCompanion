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
    public DbSet<RawGatheringEvent> RawGatheringEvents => Set<RawGatheringEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PriceCache>().HasKey(priceCache => new { priceCache.ItemId, priceCache.Location });

        modelBuilder.Entity<RawGatheringEvent>(entity =>
        {
            entity.HasIndex(e => e.SessionId);
            entity.HasIndex(e => e.Timestamp);
            // SetNull, not the default Restrict: RawGatheringEvent is an independent audit trail
            // that should outlive its session - GatheringSessionService.EndSessionAsync deletes
            // empty sessions outright (see its "no activity" branch), and virtually every session
            // has at least one raw event recorded against it (RawEventRecorder logs everything).
            // Restrict silently blocked every such delete with a FOREIGN KEY constraint failure
            // until ZoneTracker.OnError's logging surfaced it (confirmed via live capture on
            // 2026-07-18) - SetNull keeps the raw events, just detaches them from the deleted session.
            entity.HasOne(e => e.Session)
                .WithMany()
                .HasForeignKey(e => e.SessionId)
                .IsRequired(false)
                .OnDelete(DeleteBehavior.SetNull);
        });
    }
}
