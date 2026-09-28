using System.Diagnostics;
using System.Security.Cryptography;

namespace VdaHubServerController.Services;

public static class PcWindowsServiceManager
{
    public const string ServiceName = "VDAKorPcAgent";
    public const string DisplayName = "VDAKor PC Agent";
    public const string Description = "Kişisel Windows bilgisayarını VDAKor Hub ile güvenli biçimde eşleştirir ve izin verilen güç komutlarını çalıştırır.";

    public static (bool Success, string Output) InstallService(string exePath)
    {
        try
        {
            PcConfigService.EnsureProtectedInstallDirectory();
            var source = Path.GetFullPath(exePath);
            var installedExe = Path.Combine(PcConfigService.InstallDirectory, "VDAKorPcAgent.exe");

            if (IsServiceInstalled())
            {
                var configuration = RunSc($"qc \"{ServiceName}\"");
                if (!configuration.Success ||
                    !configuration.Output.Contains("--pc-service", StringComparison.OrdinalIgnoreCase))
                    return (false, "Aynı adlı fakat VDAKor PC Agent'a ait olduğu doğrulanamayan bir servis bulundu.");

                if (File.Exists(installedExe) &&
                    configuration.Output.Contains(installedExe, StringComparison.OrdinalIgnoreCase) &&
                    (source.Equals(Path.GetFullPath(installedExe), StringComparison.OrdinalIgnoreCase) ||
                     SHA256.HashData(File.ReadAllBytes(source)).SequenceEqual(SHA256.HashData(File.ReadAllBytes(installedExe)))))
                    return (true, $"'{DisplayName}' güncel EXE ile zaten kurulu.");

                StopService();
                var deleted = RunSc($"delete \"{ServiceName}\"");
                if (!deleted.Success) return (false, "Önceki PC Agent servisi kaldırılamadı: " + deleted.Output);
                var deadline = DateTime.UtcNow.AddSeconds(20);
                while (IsServiceInstalled() && DateTime.UtcNow < deadline) Thread.Sleep(250);
                if (IsServiceInstalled())
                    return (false, "Önceki PC Agent servisi silinmeyi bekliyor. Hizmetler penceresini kapatıp yeniden deneyin.");
            }

            if (!source.Equals(Path.GetFullPath(installedExe), StringComparison.OrdinalIgnoreCase))
                File.Copy(source, installedExe, overwrite: true);

            var binPath = $"\"{installedExe}\" --pc-service";
            var created = RunSc($"create \"{ServiceName}\" binPath= \"{binPath}\" start= auto DisplayName= \"{DisplayName}\"");
            if (!created.Success) return created;
            RunSc($"description \"{ServiceName}\" \"{Description}\"");
            return (true, $"'{DisplayName}' kuruldu (Otomatik Başlatma).");
        }
        catch (Exception ex)
        {
            return (false, "PC Agent servis kurulumu başarısız: " + ex.Message);
        }
    }

    public static (bool Success, string Output) StartService() =>
        GetServiceStatus().StartsWith("Çalışıyor", StringComparison.Ordinal)
            ? (true, $"'{DisplayName}' zaten çalışıyor.")
            : RunSc($"start \"{ServiceName}\"");

    public static (bool Success, string Output) StopService() =>
        RunSc($"stop \"{ServiceName}\"");

    public static (bool Success, string Output) UninstallService()
    {
        StopService();
        return RunSc($"delete \"{ServiceName}\"");
    }

    public static bool IsServiceInstalled()
    {
        var result = RunSc($"query \"{ServiceName}\"");
        return result.Success && !result.Output.Contains("1060");
    }

    public static string GetServiceStatus()
    {
        var result = RunSc($"query \"{ServiceName}\"");
        if (!result.Success) return "Yüklü Değil";
        if (result.Output.Contains("RUNNING")) return "Çalışıyor (RUNNING)";
        if (result.Output.Contains("STOPPED")) return "Durduruldu (STOPPED)";
        return "Bilinmiyor";
    }

    private static (bool Success, string Output) RunSc(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(psi);
            if (process is null) return (false, "sc.exe başlatılamadı.");
            var output = (process.StandardOutput.ReadToEnd() + "\n" + process.StandardError.ReadToEnd()).Trim();
            process.WaitForExit(5000);
            return (process.ExitCode == 0, output);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
