using AlbionCompanion.Sniffer.Protocol16;

namespace AlbionCompanion.Sniffer.AlbionEvents;

public class AlbionEventLogger
{
    private readonly string _logFilePath;
    private readonly Func<DateTime> _nowProvider;

    public AlbionEventLogger(IPhotonParser photonParser, string logFilePath, Func<DateTime>? nowProvider = null)
    {
        _logFilePath = logFilePath;
        _nowProvider = nowProvider ?? (() => DateTime.Now);
        photonParser.OnEventReceived += (_, e) => _ = WriteEventAsync(e);
        photonParser.OnResponseReceived += (_, r) => _ = WriteResponseAsync(r);
        photonParser.OnRequestReceived += (_, r) => _ = WriteRequestAsync(r);
    }

    internal Task WriteEventAsync(PhotonEvent photonEvent) =>
        WriteLineAsync($"[{Timestamp()}] EVENT code={photonEvent.Code} params={FormatParams(photonEvent.Parameters)}");

    internal Task WriteResponseAsync(PhotonResponse photonResponse) =>
        WriteLineAsync($"[{Timestamp()}] RESPONSE opCode={photonResponse.OperationCode} returnCode={photonResponse.ReturnCode} params={FormatParams(photonResponse.Parameters)}");

    internal Task WriteRequestAsync(PhotonRequest photonRequest) =>
        WriteLineAsync($"[{Timestamp()}] REQUEST opCode={photonRequest.OperationCode} params={FormatParams(photonRequest.Parameters)}");

    private string Timestamp() => _nowProvider().ToString("yyyy-MM-dd HH:mm:ss.fff");

    private static string FormatParams(Dictionary<byte, object?> parameters) =>
        "{" + string.Join(", ", parameters.Select(kv => $"{kv.Key}:{kv.Value}")) + "}";

    private async Task WriteLineAsync(string line)
    {
        var directory = Path.GetDirectoryName(_logFilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.AppendAllTextAsync(_logFilePath, line + Environment.NewLine);
    }
}
