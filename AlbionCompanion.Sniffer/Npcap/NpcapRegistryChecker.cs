using Microsoft.Win32;

namespace AlbionCompanion.Sniffer.Npcap;

public class NpcapRegistryChecker : INpcapChecker
{
    public bool IsInstalled()
    {
        // Npcap's installer registers under the 32-bit (WOW6432Node) view even on 64-bit Windows,
        // so a 64-bit process checking the default view alone will report "not installed" incorrectly.
        return IsInstalledInView(RegistryView.Registry64) || IsInstalledInView(RegistryView.Registry32);
    }

    private static bool IsInstalledInView(RegistryView view)
    {
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
        using var key = baseKey.OpenSubKey(@"SOFTWARE\Npcap");
        return key is not null;
    }
}
