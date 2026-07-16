using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace AlbionCompanion.Gathering;

// Zone name/type lookup sourced from ao-data/ao-bin-dumps' zones.json (mirrored by
// Nouuu/Albion-Online-OpenRadar, which tracks the same upstream file), keyed by the numeric
// zoneId observed in Photon RESPONSE parameter 8 (see ZoneTracker). Confirmed via live capture
// on 2026-07-16 that this correctly separates a real gathering zone (e.g. 4213 "Cairn Camain",
// type OPENPVP_YELLOW) from a city's own sub-areas like its bank/market (4001/4002, both
// PLAYERCITY_SAFEAREA_NOFURNITURE) - all of which share the outer city's zone-change wire
// signature, so the numeric id alone can't tell them apart without this lookup.
public class ZoneCatalog : IZoneCatalog
{
    private const string ZonesJsonUrl =
        "https://raw.githubusercontent.com/Nouuu/Albion-Online-OpenRadar/main/web/ao-bin-dumps/zones.json";

    private static readonly string[] SafeZoneTypePrefixes = { "PLAYERCITY", "SAFEAREA", "STARTINGCITY", "TUTORIAL" };

    private readonly HttpClient _httpClient;
    private Dictionary<int, ZoneInfo>? _zones;

    public ZoneCatalog(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<ZoneInfo?> GetZoneAsync(int zoneId)
    {
        var zones = await EnsureLoadedAsync();
        return zones.GetValueOrDefault(zoneId);
    }

    public async Task<bool> IsCityOrSafeAreaAsync(int zoneId)
    {
        var zone = await GetZoneAsync(zoneId);

        // An unrecognized zoneId is most likely a dynamic instance (dungeon, hideout, Mists)
        // not present in the static dump - those are gathering-eligible, not safe areas, so
        // default to "open world" rather than silently ignoring the transition.
        return zone is not null && SafeZoneTypePrefixes.Any(prefix => zone.Type.StartsWith(prefix, StringComparison.Ordinal));
    }

    private async Task<Dictionary<int, ZoneInfo>> EnsureLoadedAsync()
    {
        if (_zones is not null)
        {
            return _zones;
        }

        var raw = await _httpClient.GetFromJsonAsync<Dictionary<string, ZoneJsonEntry>>(ZonesJsonUrl)
                  ?? new Dictionary<string, ZoneJsonEntry>();

        _zones = raw
            .Where(entry => int.TryParse(entry.Key, out _))
            .ToDictionary(entry => int.Parse(entry.Key), entry => new ZoneInfo(entry.Value.Name, entry.Value.Type));

        return _zones;
    }

    private sealed class ZoneJsonEntry
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;
    }
}
