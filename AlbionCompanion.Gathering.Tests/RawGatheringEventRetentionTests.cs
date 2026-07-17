using Xunit;

namespace AlbionCompanion.Gathering.Tests;

public class RawGatheringEventRetentionTests
{
    [Fact]
    public void Period_IsSevenDays()
    {
        Assert.Equal(TimeSpan.FromDays(7), RawGatheringEventRetention.Period);
    }
}
