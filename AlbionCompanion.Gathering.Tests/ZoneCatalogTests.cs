using System.Net;
using System.Text;
using Xunit;

namespace AlbionCompanion.Gathering.Tests;

public class ZoneCatalogTests
{
    private sealed class CountingHandler : HttpMessageHandler
    {
        public int RequestCount;
        private readonly TaskCompletionSource _firstRequestArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseResponses = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task FirstRequestArrived => _firstRequestArrived.Task;

        public void ReleaseResponses() => _releaseResponses.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref RequestCount);
            _firstRequestArrived.TrySetResult();
            await _releaseResponses.Task;

            var json = "{\"4213\":{\"name\":\"Cairn Camain\",\"type\":\"OPENPVP_YELLOW\"}}";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        }
    }

    [Fact]
    public async Task ConcurrentLookups_OnlyFetchOnce()
    {
        // Regression: EnsureLoadedAsync used to have no synchronization, so two zone lookups
        // racing before the first fetch completes would each independently start their own HTTP
        // request instead of sharing one - wasteful at best, and a source of a whichever-finishes-
        // last-wins clobber at worst. Confirmed live on 2026-07-17 as the likely cause of a run
        // where zone recognition silently broke for the rest of the app's life.
        var handler = new CountingHandler();
        var client = new HttpClient(handler);
        var catalog = new ZoneCatalog(client);

        var lookup1 = catalog.GetZoneAsync(4213);
        var lookup2 = catalog.GetZoneAsync(4213);

        await handler.FirstRequestArrived;
        handler.ReleaseResponses();

        var results = await Task.WhenAll(lookup1, lookup2);

        Assert.Equal(1, handler.RequestCount);
        Assert.Equal("Cairn Camain", results[0]!.Name);
        Assert.Equal("Cairn Camain", results[1]!.Name);
    }

    [Fact]
    public async Task SecondLookup_AfterFirstCompletes_DoesNotRefetch()
    {
        var handler = new CountingHandler();
        var client = new HttpClient(handler);
        var catalog = new ZoneCatalog(client);
        handler.ReleaseResponses();

        await catalog.GetZoneAsync(4213);
        await catalog.GetZoneAsync(4213);

        Assert.Equal(1, handler.RequestCount);
    }

    private sealed class StaticZonesHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Regression: confirmed via live capture on 2026-08-02 - zone 4208 "Mawar Gorge" is a
            // real, gatherable royal-continent open-world zone (same WRL world-zone file naming as
            // 4213 "Cairn Camain") whose type is bare "SAFEAREA" (no PLAYERCITY prefix) because it
            // happens to be PvP-safe for bordering a city. IsCityOrSafeAreaAsync used to match any
            // "SAFEAREA"-prefixed type, wrongly treating it as a city sub-area (like the bank/market
            // zones 4001/4002) and silently refusing to start a gathering session there.
            var json = "{"
                + "\"4208\":{\"name\":\"Mawar Gorge\",\"type\":\"SAFEAREA\"},"
                + "\"4001\":{\"name\":\"Bank of Fort Sterling\",\"type\":\"PLAYERCITY_SAFEAREA_NOFURNITURE\"}"
                + "}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }

    [Fact]
    public async Task IsCityOrSafeAreaAsync_BareSafeAreaType_IsNotTreatedAsCity()
    {
        var catalog = new ZoneCatalog(new HttpClient(new StaticZonesHandler()));

        Assert.False(await catalog.IsCityOrSafeAreaAsync(4208));
    }

    [Fact]
    public async Task IsCityOrSafeAreaAsync_PlayerCitySafeAreaType_IsTreatedAsCity()
    {
        var catalog = new ZoneCatalog(new HttpClient(new StaticZonesHandler()));

        Assert.True(await catalog.IsCityOrSafeAreaAsync(4001));
    }
}
