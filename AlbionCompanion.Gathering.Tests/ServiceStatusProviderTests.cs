using Xunit;

namespace AlbionCompanion.Gathering.Tests;

public class ServiceStatusProviderTests
{
    private sealed class FakeServiceStatusProvider : IServiceStatusProvider
    {
        public ServiceStatus Status { get; set; } = ServiceStatus.Stopped;
        public int StartCallCount { get; private set; }
        public Task<ServiceStatus> GetStatusAsync() => Task.FromResult(Status);
        public Task StartAsync() { StartCallCount++; Status = ServiceStatus.Running; return Task.CompletedTask; }
    }

    [Fact]
    public async Task StartAsync_TransitionsStoppedToRunning()
    {
        var provider = new FakeServiceStatusProvider();

        await provider.StartAsync();

        Assert.Equal(ServiceStatus.Running, await provider.GetStatusAsync());
        Assert.Equal(1, provider.StartCallCount);
    }
}
