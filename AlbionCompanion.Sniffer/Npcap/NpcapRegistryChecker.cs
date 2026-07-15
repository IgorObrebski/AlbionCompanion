using Microsoft.Win32;

namespace AlbionCompanion.Sniffer.Npcap;

public class NpcapRegistryChecker : INpcapChecker
{
    public bool IsInstalled()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Npcap");
        return key is not null;
    }
}
