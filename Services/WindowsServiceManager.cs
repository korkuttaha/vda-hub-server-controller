using System.Diagnostics;

namespace VdaHubServerController.Services;

public static class WindowsServiceManager
{
    public const string ServiceName = "VdaHubServerController";
    public const string DisplayName = "VDA Hub Server Controller";
    public const string Description = "Sunucu disk ve HDD durumlarını izler, VDA Hub ve Brevo bildirimlerini iletir.";

    public static (bool Success, string Output) InstallService(string exePath)
    {
        string binPath = $"\"{exePath}\" --service";
        var res1 = RunSc($"create \"{ServiceName}\" binPath= \"{binPath}\" start= auto DisplayName= \"{DisplayName}\"");
        if (!res1.Success) return res1;

        RunSc($"description \"{ServiceName}\" \"{Description}\"");
        return (true, $"'{DisplayName}' başarıyla Windows Servisi olarak yüklendi (Otomatik Başlatma).");
    }

    public static (bool Success, string Output) UninstallService()
    {
        StopService();
        return RunSc($"delete \"{ServiceName}\"");
    }

    public static (bool Success, string Output) StartService()
    {
        return RunSc($"start \"{ServiceName}\"");
    }

    public static (bool Success, string Output) StopService()
    {
        return RunSc($"stop \"{ServiceName}\"");
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
