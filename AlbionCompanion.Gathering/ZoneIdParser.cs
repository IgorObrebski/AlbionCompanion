namespace AlbionCompanion.Gathering;

public sealed record ParsedZoneId(int? NumericZoneId, bool IsMists, string RawValue);

// Defensively classifies the raw "current zone" value from a Photon zone-change response
// (parameter 8 - see ZoneTracker). Every numeric zone id observed so far fits the first branch;
// the second and third branches exist for dynamic instances (dungeons, hideouts, the Mists),
// which per specs/albion-companion-context.md use non-numeric ids in practice - no live-capture
// sample confirms the exact shape, so this never throws regardless of what shows up: an
// unrecognized shape simply falls through to the last, safe branch instead of failing.
public static class ZoneIdParser
{
    private const string MistsPrefix = "@MISTS@";

    public static ParsedZoneId Parse(object? zoneIdValue)
    {
        if (zoneIdValue is int numeric)
        {
            return new ParsedZoneId(numeric, IsMists: false, RawValue: numeric.ToString());
        }

        var raw = zoneIdValue?.ToString() ?? string.Empty;

        if (raw.StartsWith(MistsPrefix, StringComparison.Ordinal))
        {
            return new ParsedZoneId(NumericZoneId: null, IsMists: true, RawValue: raw);
        }

        var dashIndex = raw.IndexOf('-');
        if (dashIndex > 0 && int.TryParse(raw[..dashIndex], out var prefixZoneId))
        {
            return new ParsedZoneId(prefixZoneId, IsMists: false, RawValue: raw);
        }

        return new ParsedZoneId(NumericZoneId: null, IsMists: false, RawValue: raw);
    }
}
