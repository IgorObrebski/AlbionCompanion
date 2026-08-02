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

    // Bare "SAFEAREA" (no PLAYERCITY prefix) is NOT a city/bank/market sub-area - confirmed via
    // live capture on 2026-08-02: zone 4208 "Mawar Gorge" (file 4208_WRL_MN_AUTO_T4_UND_ROY, same
    // WRL world-zone naming as gatherable zone 4213 "Cairn Camain") has type exactly "SAFEAREA".
    // It's a real, gatherable royal-continent open-world zone that just happens to be PvP-safe
    // because it borders a city - unrelated to a city's own PLAYERCITY_SAFEAREA_* sub-zones
    // (bank/market). Matching on bare "SAFEAREA" wrongly treated every one of these zones as a
    // city area and silently refused to start a gathering session in them.
    private static readonly string[] SafeZoneTypePrefixes = { "PLAYERCITY", "STARTINGCITY", "TUTORIAL" };

    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _loadLock = new(1, 1);
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

    // Single-flight guard: without this, two zone-change responses arriving close together (a
    // realistic scenario - e.g. a fast run through a city gate) could each see _zones as null and
    // independently kick off their own fetch of the same URL. Whichever finishes last would win
    // and overwrite _zones, wasting a request at best; at worst, one of the two concurrent
    // requests degrades (truncated body, transient network hiccup) without throwing and silently
    // clobbers an already-successful load, permanently breaking every future zone lookup for the
    // rest of the process's life with no error trail (nothing here used to log). This lock ensures
    // the fetch happens at most once, ever, and every caller (concurrent or not) awaits the same
    // result.
    private async Task<Dictionary<int, ZoneInfo>> EnsureLoadedAsync()
    {
        if (_zones is not null)
        {
            return _zones;
        }

        await _loadLock.WaitAsync();
        try
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
        finally
        {
            _loadLock.Release();
        }
    }

    private sealed class ZoneJsonEntry
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;
    }
}
