using AlbionCompanion.Sniffer.AlbionEvents;
using AlbionCompanion.Sniffer.Protocol16;
using Xunit;

namespace AlbionCompanion.Sniffer.Tests.AlbionEvents;

public class AlbionEventLoggerTests
{
    private sealed class FakePhotonParser : IPhotonParser
    {
        public event EventHandler<PhotonEvent>? OnEventReceived;
        public event EventHandler<PhotonResponse>? OnResponseReceived;
        public void HandlePayload(byte[] payload) { }
    }

    [Fact]
    public async Task WriteEventAsync_AppendsFormattedLine()
    {
        var logPath = Path.Combine(Path.GetTempPath(), $"albion-test-{Guid.NewGuid()}.log");
        var fixedTime = new DateTime(2026, 7, 15, 10, 30, 0, 123);
        var logger = new AlbionEventLogger(new FakePhotonParser(), logPath, () => fixedTime);

        await logger.WriteEventAsync(new PhotonEvent(42, new Dictionary<byte, object> { [0] = "T4_ORE", [1] = 5 }));

        var lines = await File.ReadAllLinesAsync(logPath);
        Assert.Equal(new[] { "[2026-07-15 10:30:00.123] EVENT code=42 params={0:T4_ORE, 1:5}" }, lines);

        File.Delete(logPath);
    }

    [Fact]
    public async Task WriteResponseAsync_AppendsFormattedLine()
    {
        var logPath = Path.Combine(Path.GetTempPath(), $"albion-test-{Guid.NewGuid()}.log");
        var fixedTime = new DateTime(2026, 7, 15, 10, 30, 1, 5);
        var logger = new AlbionEventLogger(new FakePhotonParser(), logPath, () => fixedTime);

        await logger.WriteResponseAsync(new PhotonResponse(7, 1, "debug", new Dictionary<byte, object> { [0] = "OK" }));

        var lines = await File.ReadAllLinesAsync(logPath);
        Assert.Equal(new[] { "[2026-07-15 10:30:01.005] RESPONSE opCode=7 returnCode=1 params={0:OK}" }, lines);

        File.Delete(logPath);
    }

    private sealed class RaisablePhotonParser : IPhotonParser
    {
        public event EventHandler<PhotonEvent>? OnEventReceived;
        public event EventHandler<PhotonResponse>? OnResponseReceived;
        public void HandlePayload(byte[] payload) { }
        public void RaiseEvent(PhotonEvent photonEvent) => OnEventReceived?.Invoke(this, photonEvent);
    }

    [Fact]
    public async Task Constructor_SubscribesToOnEventReceived()
    {
        var logPath = Path.Combine(Path.GetTempPath(), $"albion-test-{Guid.NewGuid()}.log");
        var parser = new RaisablePhotonParser();
        _ = new AlbionEventLogger(parser, logPath, () => new DateTime(2026, 7, 15));

        parser.RaiseEvent(new PhotonEvent(1, new Dictionary<byte, object>()));

        for (var i = 0; i < 20 && !File.Exists(logPath); i++)
        {
            await Task.Delay(50);
        }

        Assert.True(File.Exists(logPath));
        File.Delete(logPath);
    }
}
