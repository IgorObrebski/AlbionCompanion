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
}
