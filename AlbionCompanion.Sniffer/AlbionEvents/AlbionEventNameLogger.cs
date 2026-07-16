using AlbionCompanion.Sniffer.Protocol16;

namespace AlbionCompanion.Sniffer.AlbionEvents;

// Companion to AlbionEventLogger: annotates recognized event codes with their AlbionEventCode name
// so debug_packets.log stays raw while this stream is readable during discovery/testing.
public class AlbionEventNameLogger
{
    private readonly string _logFilePath;
    private readonly Func<DateTime> _nowProvider;

    public AlbionEventNameLogger(IPhotonParser photonParser, string logFilePath, Func<DateTime>? nowProvider = null)
    {
        _logFilePath = logFilePath;
        _nowProvider = nowProvider ?? (() => DateTime.Now);
        photonParser.OnEventReceived += (_, e) => _ = WriteEventAsync(e);
    }

    // Parameter 252 carries the actual semantic event type (confirmed against live captures:
    // e.g. 59/61/73/82 lining up exactly with HarvestStart/HarvestFinished/ChatMessage/UpdateFame
    // frequencies). The outer EventData.Code (near-always 1 or 3) is just a generic wrapper/
    // channel marker, not the event identity AlbionEventCode was written to describe.
    private const byte SemanticEventCodeParameterKey = 252;

    internal Task WriteEventAsync(PhotonEvent photonEvent)
    {
        byte rawCode;
        if (photonEvent.Parameters.TryGetValue(SemanticEventCodeParameterKey, out var semanticCode) && semanticCode is not null)
        {
            // Confirmed via live capture: Convert.ToByte throws OverflowException when this
            // parameter decodes to a value outside 0-255 (it isn't always a small "code" byte -
            // depends on which Photon type encoded it on the wire). A value that large can't be
            // one of our mapped AlbionEventCode values anyway, so just skip the line instead of
            // throwing - a throw here propagates out of the live Photon parse loop and aborts
            // every other command bundled in the same UDP packet (see AlbionPhotonParser).
            if (!TryToByte(semanticCode, out rawCode))
            {
                return Task.CompletedTask;
            }
        }
        else
        {
            rawCode = photonEvent.Code;
        }

        if (!AlbionEventCodeMapper.TryMap(rawCode, out var mapped))
        {
            return Task.CompletedTask;
        }

        return WriteLineAsync($"[{Timestamp()}] {mapped} code={rawCode}");
    }

    private static bool TryToByte(object value, out byte result)
    {
        var numeric = Convert.ToInt64(value);
        if (numeric is >= byte.MinValue and <= byte.MaxValue)
        {
            result = (byte)numeric;
            return true;
        }

        result = 0;
        return false;
    }

    private string Timestamp() => _nowProvider().ToString("yyyy-MM-dd HH:mm:ss.fff");

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
