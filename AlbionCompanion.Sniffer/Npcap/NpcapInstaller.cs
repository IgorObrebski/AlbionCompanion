using System.Diagnostics;

namespace AlbionCompanion.Sniffer.Npcap;

public class NpcapInstaller
{
    // Update this URL/filename when a new stable Npcap release ships (checked https://npcap.com/#download).
    private const string DownloadUrl = "https://npcap.com/dist/npcap-1.88.exe";

    private readonly INpcapChecker _checker;
    private readonly HttpClient _httpClient;

    public NpcapInstaller(INpcapChecker checker, HttpClient httpClient)
    {
        _checker = checker;
        _httpClient = httpClient;
    }

    public async Task EnsureInstalledAsync()
    {
        if (_checker.IsInstalled())
        {
            return;
        }

        var installerPath = Path.Combine(Path.GetTempPath(), "npcap-installer.exe");

        await using (var fileStream = File.Create(installerPath))
        await using (var downloadStream = await _httpClient.GetStreamAsync(DownloadUrl))
        {
            await downloadStream.CopyToAsync(fileStream);
        }

        using var process = Process.Start(new ProcessStartInfo(installerPath)
        {
            UseShellExecute = true,
            Verb = "runas"
        });

        if (process is not null)
        {
            await process.WaitForExitAsync();
        }
    }
}
