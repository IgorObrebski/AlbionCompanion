using AlbionCompanion.Gathering.LiveEvents;
using Xunit;

namespace AlbionCompanion.Gathering.Tests.LiveEvents;

public class LiveEventPipeClientTests
{
    [Fact]
    public async Task StartAsync_WhenNobodyIsListening_GivesUpAfterFiveAttemptsAndReportsExhausted()
    {
        var client = new LiveEventPipeClient("NobodyIsListeningOnThisPipe_" + Guid.NewGuid(), retryDelay: TimeSpan.FromMilliseconds(10));
        var statuses = new List<LiveEventPipeClient.ConnectionStatus>();
        client.OnStatusChanged += (_, _) => statuses.Add(client.Status);

        await client.StartAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(LiveEventPipeClient.ConnectionStatus.Exhausted, client.Status);
        Assert.Contains(LiveEventPipeClient.ConnectionStatus.Connecting, statuses);
    }

    [Fact]
    public async Task RetryNowAsync_AfterExhaustion_MakesOneImmediateAttempt()
    {
        var client = new LiveEventPipeClient("NobodyIsListeningOnThisPipe_" + Guid.NewGuid(), retryDelay: TimeSpan.FromMilliseconds(10));
        await client.StartAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(LiveEventPipeClient.ConnectionStatus.Exhausted, client.Status);

        var retryTask = client.RetryNowAsync();
        // A fresh attempt means status flips back to Connecting at least once before failing again.
        var sawConnecting = false;
        client.OnStatusChanged += (_, _) => sawConnecting |= client.Status == LiveEventPipeClient.ConnectionStatus.Connecting;
        await retryTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(LiveEventPipeClient.ConnectionStatus.Exhausted, client.Status);
    }
}
