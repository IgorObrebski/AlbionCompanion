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
    public void PlainNumericString_ReturnsNumericZoneId()
    {
        // Confirmed via live capture on 2026-07-18: PhotonPackageParser decodes zone id parameter
        // 8 as a System.String even for plain city/open-world zones (e.g. "4000", "4203"), never
        // a boxed int - this is the real-world shape, not BoxedInt_ReturnsNumericZoneId above.
        var result = ZoneIdParser.Parse("4203");

        Assert.Equal(4203, result.NumericZoneId);
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
