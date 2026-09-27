using System.Diagnostics;
using System.Security.Cryptography;

namespace VdaHubServerController.Services;

public static class WindowsServiceManager
{
    public const string ServiceName = "VdaHubServerController";
    public const string DisplayName = "VDA Hub Server Controller";
    public const string Description = "Sunucu disk/CPU/RAM durumunu izler, VDA Hub raporlarını ve güvenli Dropbox yedeklemeyi çalıştırır.";

    public static (bool Success, string Output) InstallService(string exePath)
    {
        try
        {
            ConfigService.EnsureProtectedInstallDirectory();
            var source = Path.GetFullPath(exePath);
            var installedExe = Path.Combine(ConfigService.InstallDirectory, "VdaHubServerController.exe");
            var replacedPreviousService = false;

            if (IsServiceInstalled())
            {
                var configuration = RunSc($"qc \"{ServiceName}\"");
                if (!configuration.Success ||
                    !configuration.Output.Contains("--service", StringComparison.OrdinalIgnoreCase) ||
                    !(configuration.Output.Contains("VdaHubServerController.exe", StringComparison.OrdinalIgnoreCase) ||
                      configuration.Output.Contains("vda-hub-server-controller.exe", StringComparison.OrdinalIgnoreCase)))
                {
                    return (false, "Aynı adlı fakat VDAKor ajanına ait olduğu doğrulanamayan bir servis bulundu; hiçbir değişiklik yapılmadı.");
                }

                if (File.Exists(installedExe) &&
                    configuration.Output.Contains(installedExe, StringComparison.OrdinalIgnoreCase) &&
                    (source.Equals(Path.GetFullPath(installedExe), StringComparison.OrdinalIgnoreCase) ||
                     SHA256.HashData(File.ReadAllBytes(source)).SequenceEqual(SHA256.HashData(File.ReadAllBytes(installedExe)))))
                {
                    return (true, $"'{DisplayName}' güncel EXE ile zaten kurulu.");
                }

                StopService();
                var deleted = RunSc($"delete \"{ServiceName}\"");
                if (!deleted.Success) return (false, "Önceki VDAKor ajan servisi kaldırılamadı: " + deleted.Output);
                var deadline = DateTime.UtcNow.AddSeconds(20);
                while (IsServiceInstalled() && DateTime.UtcNow < deadline) Thread.Sleep(250);
                if (IsServiceInstalled())
                    return (false, "Önceki ajan servisi silinmeyi bekliyor. Hizmetler penceresini kapatıp yeniden deneyin.");
                replacedPreviousService = true;
            }

            if (!source.Equals(Path.GetFullPath(installedExe), StringComparison.OrdinalIgnoreCase))
                File.Copy(source, installedExe, overwrite: true);

            string binPath = $"\"{installedExe}\" --service";
            var res1 = RunSc($"create \"{ServiceName}\" binPath= \"{binPath}\" start= auto DisplayName= \"{DisplayName}\"");
            if (!res1.Success) return res1;

            RunSc($"description \"{ServiceName}\" \"{Description}\"");
            var prefix = replacedPreviousService ? "Önceki ajan servisi kaldırıldı; " : string.Empty;
            return (true, $"{prefix}'{DisplayName}' {installedExe} konumuna kuruldu (Otomatik Başlatma).");
        }
        catch (Exception ex)
        {
            return (false, "Güvenli servis kurulumu başarısız: " + ex.Message);
        }
    }

    public static (bool Success, string Output) UninstallService()
    {
        StopService();
        return RunSc($"delete \"{ServiceName}\"");
    }

    public static (bool Success, string Output) StartService()
    {
        if (GetServiceStatus().StartsWith("Çalışıyor", StringComparison.Ordinal))
            return (true, $"'{DisplayName}' zaten çalışıyor.");
        var result = RunSc($"start \"{ServiceName}\"");
        return result.Success || GetServiceStatus().StartsWith("Çalışıyor", StringComparison.Ordinal)
            ? (true, result.Success ? result.Output : $"'{DisplayName}' çalışıyor.")
            : result;
    }

    public static (bool Success, string Output) StopService()
    {
        return RunSc($"stop \"{ServiceName}\"");
    }

    public static (bool Success, string Output) RestartService()
    {
        if (!IsServiceInstalled())
            return (false, $"'{DisplayName}' yüklü değil; eşleştirme kaydedildi fakat servis yeniden başlatılamadı.");

        var stop = StopService();
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            var status = GetServiceStatus();
            if (status.StartsWith("Durduruldu", StringComparison.Ordinal)) break;
            Thread.Sleep(250);
        }

        if (!GetServiceStatus().StartsWith("Durduruldu", StringComparison.Ordinal))
            return (false, "Yeni eşleştirme kaydedildi fakat servis zamanında durmadı: " + stop.Output);

        var start = StartService();
        return start.Success
            ? (true, "Sunucu yeniden eşleştirildi ve headless servis yeni anahtarla yeniden başlatıldı.")
            : (false, "Yeni eşleştirme kaydedildi fakat servis yeniden başlatılamadı: " + start.Output);
    }

    public static bool IsServiceInstalled()
    {
        var res = RunSc($"query \"{ServiceName}\"");
        return res.Success && !res.Output.Contains("1060"); // 1060: ERROR_SERVICE_DOES_NOT_EXIST
    }

    public static string GetServiceStatus()
    {
        var res = RunSc($"query \"{ServiceName}\"");
        if (!res.Success) return "Yüklü Değil";

        if (res.Output.Contains("RUNNING")) return "Çalışıyor (RUNNING)";
        if (res.Output.Contains("STOPPED")) return "Durduruldu (STOPPED)";
        if (res.Output.Contains("START_PENDING")) return "Başlatılıyor...";
        if (res.Output.Contains("STOP_PENDING")) return "Durduruluyor...";

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
            if (process == null) return (false, "sc.exe başlatılamadı.");

            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit(5000);

            bool success = process.ExitCode == 0;
            string combined = (stdout + "\n" + stderr).Trim();
            return (success, combined);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
