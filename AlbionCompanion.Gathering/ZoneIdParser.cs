namespace AlbionCompanion.Gathering;

public sealed record ParsedZoneId(int? NumericZoneId, bool IsMists, string RawValue);

// Defensively classifies the raw "current zone" value from a Photon zone-change response
// (parameter 8 - see ZoneTracker). Confirmed via live capture on 2026-07-18: PhotonPackageParser
// decodes this parameter as a System.String, never a boxed int - even for plain numeric zone ids
// (e.g. "4000", "4203") - so the numeric check must parse the string, not pattern-match on `int`.
// The dash-prefix and Mists-prefix branches exist for dynamic instances (dungeons, hideouts, the
// Mists), which per specs/albion-companion-context.md use non-numeric ids in practice - an
// unrecognized shape simply falls through to the last, safe branch instead of failing.
public static class ZoneIdParser
{
    private const string MistsPrefix = "@MISTS@";

    public static ParsedZoneId Parse(object? zoneIdValue)
    {
        var raw = zoneIdValue?.ToString() ?? string.Empty;

        if (raw.StartsWith(MistsPrefix, StringComparison.Ordinal))
        {
            return new ParsedZoneId(NumericZoneId: null, IsMists: true, RawValue: raw);
        }

        if (int.TryParse(raw, out var numeric))
        {
            return new ParsedZoneId(numeric, IsMists: false, RawValue: raw);
        }

        var dashIndex = raw.IndexOf('-');
        if (dashIndex > 0 && int.TryParse(raw[..dashIndex], out var prefixZoneId))
        {
            return new ParsedZoneId(prefixZoneId, IsMists: false, RawValue: raw);
        }

        return new ParsedZoneId(NumericZoneId: null, IsMists: false, RawValue: raw);
    }
}
