using Xunit;

namespace AlbionCompanion.Gathering.Tests;

public class ZoneIdParserTests
{
    [Fact]
    public void BoxedInt_ReturnsNumericZoneId()
    {
        var result = ZoneIdParser.Parse(4213);

        Assert.Equal(4213, result.NumericZoneId);
        Assert.False(result.IsMists);
    }

    [Fact]
    public void MistsPrefixedString_ReturnsIsMists()
    {
        var result = ZoneIdParser.Parse("@MISTS@some-guid-looking-string");

        Assert.True(result.IsMists);
        Assert.Null(result.NumericZoneId);
    }

    [Fact]
    public void NumericPrefixedInstanceId_ReturnsBaseZoneId()
    {
        var result = ZoneIdParser.Parse("1234-5");

        Assert.Equal(1234, result.NumericZoneId);
        Assert.False(result.IsMists);
    }

    [Fact]
    public void CompletelyUnrecognizedString_ReturnsNullWithRawValue()
    {
        var result = ZoneIdParser.Parse("garbage");

        Assert.Null(result.NumericZoneId);
        Assert.False(result.IsMists);
        Assert.Equal("garbage", result.RawValue);
    }

    [Fact]
    public void NonNumericPrefixBeforeDash_FallsThroughToUnrecognized()
    {
        var result = ZoneIdParser.Parse("abc-5");

        Assert.Null(result.NumericZoneId);
        Assert.False(result.IsMists);
    }

    [Fact]
    public void NullValue_DoesNotThrow_ReturnsUnrecognized()
    {
        var result = ZoneIdParser.Parse(null);

        Assert.Null(result.NumericZoneId);
        Assert.False(result.IsMists);
        Assert.Equal(string.Empty, result.RawValue);
    }
}
