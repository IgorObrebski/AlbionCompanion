using AlbionCompanion.Sniffer.Protocol16;
using Xunit;

namespace AlbionCompanion.Sniffer.Tests.Protocol16;

public class AlbionPhotonParserTests
{
    private sealed class TestablePhotonParser : AlbionPhotonParser
    {
        public void InvokeOnEvent(byte code, Dictionary<byte, object> parameters) => OnEvent(code, parameters);

        public void InvokeOnResponse(byte operationCode, short returnCode, string debugMessage, Dictionary<byte, object> parameters) =>
            OnResponse(operationCode, returnCode, debugMessage, parameters);

        public void InvokeOnRequest(byte operationCode, Dictionary<byte, object> parameters) => OnRequest(operationCode, parameters);
    }

    [Fact]
    public void OnEvent_RaisesOnEventReceivedWithMappedData()
    {
        var parser = new TestablePhotonParser();
        PhotonEvent? received = null;
        parser.OnEventReceived += (_, e) => received = e;
        var parameters = new Dictionary<byte, object> { [0] = "T4_ORE", [1] = 5 };

        parser.InvokeOnEvent(42, parameters);

        Assert.NotNull(received);
        Assert.Equal((byte)42, received!.Code);
        Assert.Same(parameters, received.Parameters);
    }

    [Fact]
    public void OnResponse_RaisesOnResponseReceivedWithMappedData()
    {
        var parser = new TestablePhotonParser();
        PhotonResponse? received = null;
        parser.OnResponseReceived += (_, r) => received = r;
        var parameters = new Dictionary<byte, object> { [0] = "OK" };

        parser.InvokeOnResponse(7, 1, "debug", parameters);

        Assert.NotNull(received);
        Assert.Equal((byte)7, received!.OperationCode);
        Assert.Equal((short)1, received.ReturnCode);
        Assert.Equal("debug", received.DebugMessage);
        Assert.Same(parameters, received.Parameters);
    }

    [Fact]
    public void OnRequest_DoesNotThrow()
    {
        var parser = new TestablePhotonParser();

        var exception = Record.Exception(() => parser.InvokeOnRequest(1, new Dictionary<byte, object>()));

        Assert.Null(exception);
    }

    [Fact]
    public void HandlePayload_DoesNotThrow_WhenUnderlyingParserFailsOnMalformedCommand()
    {
        // 12-byte Photon header (peerId=0, flags=0, commandCount=1, timestamp=0, challenge=0) with
        // no bytes left for the command it announces - PhotonPackageParser throws IndexOutOfRangeException
        // reading past the array. Real Albion traffic hits an equivalent library limitation (unimplemented
        // Photon parameter type codes) - either way, one bad payload must not kill the sniffer.
        var malformedPayload = new byte[12];
        malformedPayload[3] = 1; // commandCount = 1, but no bytes follow for that command
        var parser = new AlbionPhotonParser();

        var exception = Record.Exception(() => parser.HandlePayload(malformedPayload));

        Assert.Null(exception);
    }
}
