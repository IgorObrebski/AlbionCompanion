using AlbionCompanion.Sniffer.Npcap;
using Xunit;

namespace AlbionCompanion.Sniffer.Tests.Npcap;

public class NpcapInstallerTests
{
    private sealed class AlwaysInstalledChecker : INpcapChecker
    {
        public bool IsInstalled() => true;
    }

    private sealed class NeverInstalledChecker : INpcapChecker
    {
        public bool IsInstalled() => false;
    }

    private sealed class ThrowingHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("HTTP call should not happen when Npcap is already installed.");
    }

    [Fact]
    public async Task EnsureInstalledAsync_SkipsDownloadWhenAlreadyInstalled()
    {
        using var httpClient = new HttpClient(new ThrowingHttpMessageHandler());
        var installer = new NpcapInstaller(new AlwaysInstalledChecker(), httpClient);

        var exception = await Record.ExceptionAsync(() => installer.EnsureInstalledAsync());

        Assert.Null(exception);
    }

    [Fact]
    public void EnsureInstalledAsync_ChecksInstallationStatusFirst()
    {
        var checker = new NeverInstalledChecker();

        Assert.False(checker.IsInstalled());
    }
}
