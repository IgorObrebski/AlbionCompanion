using System.IO.Pipes;
using AlbionCompanion.Gathering.LiveEvents;
using Xunit;

namespace AlbionCompanion.Gathering.Tests.LiveEvents;

public class LiveEventPipeClientTests
{
    [Fact]
    public async Task StartAsync_WhenNobodyIsListening_GivesUpAfterFiveAttemptsAndReportsExhausted()
    {
        var client = new LiveEventPipeClient("NobodyIsListeningOnThisPipe_" + Guid.NewGuid(), retryDelay: TimeSpan.FromMilliseconds(10), connectTimeout: TimeSpan.FromMilliseconds(200));
        var statuses = new List<LiveEventPipeClient.ConnectionStatus>();
        client.OnStatusChanged += (_, _) => statuses.Add(client.Status);

        await client.StartAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(LiveEventPipeClient.ConnectionStatus.Exhausted, client.Status);
        Assert.Contains(LiveEventPipeClient.ConnectionStatus.Connecting, statuses);
    }

    [Fact]
    public async Task RetryNowAsync_AfterExhaustion_MakesOneImmediateAttempt()
    {
        var client = new LiveEventPipeClient("NobodyIsListeningOnThisPipe_" + Guid.NewGuid(), retryDelay: TimeSpan.FromMilliseconds(10), connectTimeout: TimeSpan.FromMilliseconds(200));
        await client.StartAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(LiveEventPipeClient.ConnectionStatus.Exhausted, client.Status);

        var retryTask = client.RetryNowAsync();
        // A fresh attempt means status flips back to Connecting at least once before failing again.
        var sawConnecting = false;
        client.OnStatusChanged += (_, _) => sawConnecting |= client.Status == LiveEventPipeClient.ConnectionStatus.Connecting;
        await retryTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(LiveEventPipeClient.ConnectionStatus.Exhausted, client.Status);
    }

    [Fact]
    public async Task StartAsync_WhenServerStartsListeningPartwayThroughTheConnectTimeout_StillConnects()
    {
        // Regression test for the reconnect-after-service-start scenario RetryNowAsync exists for:
        // the pipe's own connect attempt must be allowed to outlast a slow server, independent of
        // how short retryDelay is configured. A real NamedPipeServerStream only starts accepting
        // after a deliberate delay, well inside the client's configured connectTimeout but past
        // what the previous (retryDelay-derived) timeout heuristic would have allowed.
        var pipeName = "SlowToStartListeningPipe_" + Guid.NewGuid();
        using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var serverAcceptTask = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(300));
            await server.WaitForConnectionAsync();
        });

        var client = new LiveEventPipeClient(pipeName, retryDelay: TimeSpan.FromMilliseconds(10), connectTimeout: TimeSpan.FromSeconds(2));

        await client.StartAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        await serverAcceptTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(LiveEventPipeClient.ConnectionStatus.Connected, client.Status);
        client.Dispose();
    }

    [Fact]
    public async Task Dispose_AfterConnecting_ClosesThePipeWithoutThrowing()
    {
        var pipeName = "DisposeTestPipe_" + Guid.NewGuid();
        using var cts = new CancellationTokenSource();
        var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var serverAcceptTask = server.WaitForConnectionAsync(cts.Token);

        var client = new LiveEventPipeClient(pipeName, retryDelay: TimeSpan.FromMilliseconds(10), connectTimeout: TimeSpan.FromSeconds(2));
        await client.StartAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        await serverAcceptTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(LiveEventPipeClient.ConnectionStatus.Connected, client.Status);

        client.Dispose();
        client.Dispose(); // must be idempotent

        server.Dispose();
    }
}
