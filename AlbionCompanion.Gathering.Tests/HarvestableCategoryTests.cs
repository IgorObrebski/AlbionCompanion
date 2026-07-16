using Xunit;

namespace AlbionCompanion.Gathering.Tests;

public class HarvestableCategoryTests
{
    [Theory]
    [InlineData(0, "WOOD")]
    [InlineData(5, "WOOD")]
    [InlineData(6, "ROCK")]
    [InlineData(7, "ROCK")]
    [InlineData(10, "ROCK")]
    [InlineData(11, "FIBER")]
    [InlineData(14, "FIBER")]
    [InlineData(15, "FIBER")]
    [InlineData(16, "HIDE")]
    [InlineData(20, "HIDE")]
    [InlineData(22, "HIDE")]
    [InlineData(23, "ORE")]
    [InlineData(27, "ORE")]
    public void FromTypeCode_ReturnsExpectedCategory(int typeCode, string expectedCategory) =>
        Assert.Equal(expectedCategory, HarvestableCategory.FromTypeCode(typeCode));

    [Theory]
    [InlineData(28)]
    [InlineData(29)]
    [InlineData(-1)]
    public void FromTypeCode_OutOfRange_ReturnsNull(int typeCode) =>
        Assert.Null(HarvestableCategory.FromTypeCode(typeCode));
}
