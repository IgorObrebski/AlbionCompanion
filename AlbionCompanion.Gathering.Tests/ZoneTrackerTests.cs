using AlbionCompanion.Core.Data;
using AlbionCompanion.Sniffer.Protocol16;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AlbionCompanion.Gathering.Tests;

public class ZoneTrackerTests
{
    private sealed class FakePhotonParser : IPhotonParser
    {
        public event EventHandler<PhotonEvent>? OnEventReceived;
        public event EventHandler<PhotonResponse>? OnResponseReceived;
        public event EventHandler<PhotonRequest>? OnRequestReceived;
        public void HandlePayload(byte[] payload) { }
        public void RaiseResponse(PhotonResponse response) => OnResponseReceived?.Invoke(this, response);
    }

    private sealed class FakeZoneCatalog : IZoneCatalog
    {
        private readonly Dictionary<int, ZoneInfo> _zones;

        public FakeZoneCatalog(Dictionary<int, ZoneInfo> zones) => _zones = zones;

        public Task<ZoneInfo?> GetZoneAsync(int zoneId) => Task.FromResult(_zones.GetValueOrDefault(zoneId));

        public Task<bool> IsCityOrSafeAreaAsync(int zoneId) =>
            Task.FromResult(_zones.TryGetValue(zoneId, out var zone) && zone.Type.StartsWith("PLAYERCITY", StringComparison.Ordinal));
    }

    private static readonly Dictionary<int, ZoneInfo> SampleZones = new()
    {
        [4000] = new ZoneInfo("Fort Sterling", "PLAYERCITY_SAFEAREA_02"),
        [4001] = new ZoneInfo("Bank of Fort Sterling", "PLAYERCITY_SAFEAREA_NOFURNITURE"),
        [4002] = new ZoneInfo("Fort Sterling Market", "PLAYERCITY_SAFEAREA_NOFURNITURE"),
        [4213] = new ZoneInfo("Cairn Camain", "OPENPVP_YELLOW"),
    };

    private static GatheringSessionService CreateService(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        var context = new AppDbContext(options);
        context.Database.EnsureCreated();
        return new GatheringSessionService(context);
    }

    private static PhotonResponse ZoneResponse(int zoneId) =>
        new(OperationCode: 1, ReturnCode: 0, DebugMessage: string.Empty,
            Parameters: new Dictionary<byte, object?> { [253] = 2, [8] = zoneId });

    private static PhotonResponse ZoneResponse(object zoneIdValue) =>
        new(OperationCode: 1, ReturnCode: 0, DebugMessage: string.Empty,
            Parameters: new Dictionary<byte, object?> { [253] = 2, [8] = zoneIdValue });

    [Fact]
    public async Task EnteringOpenWorldZone_StartsSessionWithZoneName()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var service = CreateService(connection);
        var parser = new FakePhotonParser();
        _ = new ZoneTracker(parser, service, new FakeZoneCatalog(SampleZones));

        parser.RaiseResponse(ZoneResponse(4213)); // Cairn Camain, open world
        await Task.Delay(20);

        var active = await service.GetActiveSessionAsync();
        Assert.NotNull(active);
        Assert.Equal("Cairn Camain", active!.StartLocation);
    }

    [Fact]
    public async Task EnteringCity_EndsActiveSession()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var service = CreateService(connection);
        var parser = new FakePhotonParser();
        _ = new ZoneTracker(parser, service, new FakeZoneCatalog(SampleZones));

        parser.RaiseResponse(ZoneResponse(4213)); // left to open world
        await Task.Delay(20);
        await service.AddItemAsync("T4_ORE", 5); // something gathered, so the session survives close
        parser.RaiseResponse(ZoneResponse(4000)); // back to Fort Sterling
        await Task.Delay(20);

        Assert.Null(await service.GetActiveSessionAsync());
    }

    [Fact]
    public async Task VisitingBankAndMarket_DoesNotSpuriouslyStartOrEndSessions()
    {
        // Regression: bank/market are separate zoneIds from the main city, so a naive
        // "different from home zone" check mistook them for leaving on a gathering trip.
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var service = CreateService(connection);
        var parser = new FakePhotonParser();
        _ = new ZoneTracker(parser, service, new FakeZoneCatalog(SampleZones));

        parser.RaiseResponse(ZoneResponse(4000)); // start in city
        await Task.Delay(20);
        parser.RaiseResponse(ZoneResponse(4001)); // bank
        await Task.Delay(20);
        parser.RaiseResponse(ZoneResponse(4002)); // market
        await Task.Delay(20);
        parser.RaiseResponse(ZoneResponse(4000)); // back to main city
        await Task.Delay(20);

        Assert.Null(await service.GetActiveSessionAsync());
    }

    [Fact]
    public async Task UnrecognizedZoneId_TreatedAsOpenWorld()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var service = CreateService(connection);
        var parser = new FakePhotonParser();
        _ = new ZoneTracker(parser, service, new FakeZoneCatalog(SampleZones));

        parser.RaiseResponse(ZoneResponse(999999)); // e.g. a dynamic dungeon instance
        await Task.Delay(20);

        var active = await service.GetActiveSessionAsync();
        Assert.NotNull(active);
        Assert.Equal("999999", active!.StartLocation);
    }

    [Fact]
    public async Task ResponseWithoutZoneSubCode_IsIgnored()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var service = CreateService(connection);
        var parser = new FakePhotonParser();
        var tracker = new ZoneTracker(parser, service, new FakeZoneCatalog(SampleZones));

        await tracker.HandleResponseAsync(new PhotonResponse(1, 0, string.Empty,
            new Dictionary<byte, object?> { [253] = 52, [8] = 999 }));

        Assert.Null(await service.GetActiveSessionAsync());
    }

    [Fact]
    public async Task EnteringMists_StartsSessionNamedMists()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var service = CreateService(connection);
        var parser = new FakePhotonParser();
        _ = new ZoneTracker(parser, service, new FakeZoneCatalog(SampleZones));

        parser.RaiseResponse(ZoneResponse("@MISTS@abc-guid-looking-string"));
        await Task.Delay(20);

        var active = await service.GetActiveSessionAsync();
        Assert.NotNull(active);
        Assert.Equal("Mists", active!.StartLocation);
    }

    [Fact]
    public async Task EnteringNumericPrefixedInstance_ResolvesBaseZoneName()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var service = CreateService(connection);
        var parser = new FakePhotonParser();
        _ = new ZoneTracker(parser, service, new FakeZoneCatalog(SampleZones));

        parser.RaiseResponse(ZoneResponse("4213-5")); // dynamic instance of Cairn Camain
        await Task.Delay(20);

        var active = await service.GetActiveSessionAsync();
        Assert.NotNull(active);
        Assert.Equal("Cairn Camain", active!.StartLocation);
    }

    [Fact]
    public async Task EnteringUnrecognizedStringZoneId_DoesNotThrowAndUsesRawValue()
    {
        // Regression: ZoneTracker used to call Convert.ToInt32(zoneIdValue) unconditionally, which
        // threw FormatException on any non-numeric zone id - lost silently since dispatch is
        // fire-and-forget. This must no longer throw, and the fire-and-forget dispatch must still
        // reach GatheringSessionService.StartSessionAsync with the raw value.
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var service = CreateService(connection);
        var parser = new FakePhotonParser();
        _ = new ZoneTracker(parser, service, new FakeZoneCatalog(SampleZones));

        parser.RaiseResponse(ZoneResponse("some-unrecognized-format"));
        await Task.Delay(20);

        var active = await service.GetActiveSessionAsync();
        Assert.NotNull(active);
        Assert.Equal("some-unrecognized-format", active!.StartLocation);
    }
}
