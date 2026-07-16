namespace AlbionCompanion.Sniffer.AlbionEvents;

public static class AlbionEventCodeMapper
{
    public static bool TryMap(byte code, out AlbionEventCode mapped)
    {
        if (Enum.IsDefined(typeof(AlbionEventCode), code))
        {
            mapped = (AlbionEventCode)code;
            return true;
        }

        mapped = default;
        return false;
    }
}
