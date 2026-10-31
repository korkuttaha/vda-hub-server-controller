using System.Diagnostics;
using System.Management;
using VdaHubServerController.Models;

namespace VdaHubServerController.Services;

public static class SqlBackupServiceMonitor
{
    public const string ServiceName = "VdaMssqlBackup";

    public static SqlBackupServiceSnapshot Collect()
    {
        var snapshot = new SqlBackupServiceSnapshot { CheckedAtUtc = DateTime.UtcNow };
        try
        {
            using var query = new ManagementObjectSearcher(new ManagementScope("root\\CIMV2"),
                new ObjectQuery("SELECT State, StartMode FROM Win32_Service WHERE Name = 'VdaMssqlBackup'"),
                new System.Management.EnumerationOptions { Timeout = TimeSpan.FromSeconds(3) });
            using var services = query.Get();
            foreach (ManagementObject service in services)
            {
                using (service)
                {
                    snapshot = snapshot with
                    {
                        Status = "success",
                        Installed = true,
                        ServiceState = service["State"]?.ToString() switch
                        {
                            "Running" => "running", "Stopped" => "stopped", "Paused" => "paused",
                            "Start Pending" => "starting", "Stop Pending" => "stopping",
                            "Continue Pending" => "starting", "Pause Pending" => "pausing", _ => "unknown"
                        },
                        StartMode = service["StartMode"]?.ToString() switch
                        {
                            "Auto" => "automatic", "Manual" => "manual", "Disabled" => "disabled", _ => "unknown"
                        },
                        Version = InstalledVersion()
                    };
                }
                return SqlBackupStateReader.Read(snapshot, Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
            }
            return snapshot with { Status = "success", Installed = false };
        }
        catch (Exception error) when (error is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or InvalidOperationException or System.Security.SecurityException or TimeoutException or System.ComponentModel.Win32Exception)
        {
            return snapshot;
        }
    }

    private static string? InstalledVersion()
    {
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "VDA.MssqlBackup.Service");
            var path = Path.Combine(directory, "VdaMssqlBackup.Service.exe");
            if (!File.Exists(path) || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0 ||
                (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return null;
            var version = FileVersionInfo.GetVersionInfo(path);
            return $"{version.FileMajorPart}.{version.FileMinorPart}.{version.FileBuildPart}.{version.FilePrivatePart}";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception or System.Security.SecurityException)
        {
            return null;
        }
    }
}
