using AlbionCompanion.Gathering.LiveEvents;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AlbionCompanion.Gathering.Tests;

public class AppClientHostBuilderTests
{
    // Regression: IItemDictionaryService's real implementation needs an HttpClient in its
    // constructor - dropped by mistake when this builder was split off from AppHostBuilder,
    // and not caught until a live run navigated to a page that actually resolved it (Broadcast's
    // ItemTable), throwing an unobserved DI activation exception that crashed the WebView with no
    // trace in Windows Event Log. Resolving every registered service, not just constructing the
    // provider, is what catches a missing-dependency bug like this.
    [Fact]
    public void BuildServiceProvider_EveryRegisteredServiceResolvesWithoutThrowing()
    {
        using var tempDir = new TempDirectory();
        using var provider = AppClientHostBuilder.BuildServiceProvider(tempDir.Path);

        Assert.NotNull(provider.GetRequiredService<ICharacterService>());
        Assert.NotNull(provider.GetRequiredService<IGatheringSessionService>());
        Assert.NotNull(provider.GetRequiredService<IItemDictionaryService>());
        Assert.NotNull(provider.GetRequiredService<IServiceStatusProvider>());
        Assert.NotNull(provider.GetRequiredService<LiveEventPipeClient>());
        Assert.NotNull(provider.GetRequiredService<ILocalPlayerTracker>());
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString());

        public TempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // Best-effort cleanup.
            }
        }
    }
}
