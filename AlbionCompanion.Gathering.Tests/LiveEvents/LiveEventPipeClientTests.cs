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
    public async Task StartAsync_WhenServerDropsConnectionImmediatelyAfterAccepting_AutomaticallyReconnects()
    {
        // Regression test for the reconnect-after-drop guard race: if the server accepts and then
        // immediately closes the connection (simulating a Windows Service bouncing right after
        // accepting a client), ReadLoopAsync's own finally block races the outer StartAsync call's
        // unwind. Previously the outer StartAsync's connecting-guard release happened AFTER the read
        // loop had already been started, so a fast-enough drop could have ReadLoopAsync's finally try
        // to kick off a fresh retry cycle while the guard was still held - defeating the reconnect and
        // leaving the client stuck in Disconnected forever.
        var pipeName = "ImmediateDropPipe_" + Guid.NewGuid();
        var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var acceptTask = server.WaitForConnectionAsync();

        var client = new LiveEventPipeClient(pipeName, retryDelay: TimeSpan.FromMilliseconds(10), connectTimeout: TimeSpan.FromSeconds(2));

        var statuses = new List<LiveEventPipeClient.ConnectionStatus>();
        var connectingAfterDropSignal = new TaskCompletionSource();
        var sawConnectedOnce = false;
        client.OnStatusChanged += (_, _) =>
        {
            statuses.Add(client.Status);
            if (client.Status == LiveEventPipeClient.ConnectionStatus.Connected)
            {
                sawConnectedOnce = true;
            }
            else if (sawConnectedOnce && client.Status == LiveEventPipeClient.ConnectionStatus.Connecting)
            {
                connectingAfterDropSignal.TrySetResult();
            }
        };

        var startTask = client.StartAsync(CancellationToken.None);

        // Wait for the server to accept, then immediately drop the connection - the exact scenario
        // that previously defeated the reconnect guard.
        await acceptTask.WaitAsync(TimeSpan.FromSeconds(5));
        server.Dispose();

        // After the drop, the client must attempt to reconnect on its own - i.e. cycle back through
        // Connecting - without any external caller (App restart, Settings button click) prompting it.
        await connectingAfterDropSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(sawConnectedOnce);
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
