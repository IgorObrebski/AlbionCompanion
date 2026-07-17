using AlbionCompanion.Core.Data;
using AlbionCompanion.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AlbionCompanion.Gathering.Tests;

public class RawGatheringEventRetentionTests
{
    [Fact]
    public void Period_IsSevenDays()
    {
        Assert.Equal(TimeSpan.FromDays(7), RawGatheringEventRetention.Period);
    }

    [Fact]
    public async Task CleanupSweep_DeletesOnlyRowsOlderThanRetentionPeriod()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        using var context = new AppDbContext(options);
        context.Database.EnsureCreated();

        var oldRow = new RawGatheringEvent
        {
            PhotonCode = 1,
            ParametersJson = "{}",
            Timestamp = DateTime.UtcNow - RawGatheringEventRetention.Period - TimeSpan.FromDays(1),
        };
        var newRow = new RawGatheringEvent
        {
            PhotonCode = 2,
            ParametersJson = "{}",
            Timestamp = DateTime.UtcNow - TimeSpan.FromDays(1),
        };
        context.RawGatheringEvents.AddRange(oldRow, newRow);
        await context.SaveChangesAsync();

        var cutoff = DateTime.UtcNow - RawGatheringEventRetention.Period;
        await context.RawGatheringEvents
            .Where(e => e.Timestamp < cutoff)
            .ExecuteDeleteAsync();

        var remaining = Assert.Single(context.RawGatheringEvents);
        Assert.Equal(newRow.PhotonCode, remaining.PhotonCode);
    }
}
