using System.Management;
using VdaHubServerController.Models;

namespace VdaHubServerController.Services;

public class DiskMonitorService
{
    public ServerStatusReport CollectReport(AppConfig config)
    {
        var report = new ServerStatusReport
        {
            ServerId = config.ServerId,
            ServerName = string.IsNullOrWhiteSpace(config.ServerName) ? Environment.MachineName : config.ServerName,
            MachineName = Environment.MachineName,
            OsVersion = Environment.OSVersion.ToString(),
            Timestamp = DateTime.UtcNow
        };

        // 1. Collect Logical Drive Volumes
        string systemRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";

        try
        {
            var drives = DriveInfo.GetDrives();
            foreach (var drive in drives)
            {
                if (!drive.IsReady)
                    continue;

                // Only monitor fixed drives (HDDs, SSDs)
                if (drive.DriveType != DriveType.Fixed)
                    continue;

                long totalBytes = drive.TotalSize;
                long freeBytes = drive.AvailableFreeSpace;
                long usedBytes = totalBytes - freeBytes;
                double usedPercentage = totalBytes > 0 ? ((double)usedBytes / totalBytes) * 100.0 : 0.0;

                var volume = new DiskVolumeInfo
                {
                    Name = drive.Name,
                    VolumeLabel = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? "(İsimsiz)" : drive.VolumeLabel,
                    DriveType = drive.DriveType.ToString(),
                    DriveFormat = drive.DriveFormat,
                    TotalSizeBytes = totalBytes,
                    FreeSizeBytes = freeBytes,
                    UsedSizeBytes = usedBytes,
                    UsedPercentage = Math.Round(usedPercentage, 1),
                    IsSystemDrive = string.Equals(drive.Name, systemRoot, StringComparison.OrdinalIgnoreCase)
                };

                report.Volumes.Add(volume);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[DiskMonitor] Logical drives okuma hatası: {ex.Message}");
        }

        // 2. Collect Physical Hard Drives via WMI
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_DiskDrive");
            foreach (ManagementObject wmiDisk in searcher.Get())
            {
                string deviceId = wmiDisk["DeviceID"]?.ToString() ?? string.Empty;
                string model = wmiDisk["Model"]?.ToString()?.Trim() ?? "Bilinmeyen Disk";
                string iface = wmiDisk["InterfaceType"]?.ToString() ?? string.Empty;
                string mediaType = wmiDisk["MediaType"]?.ToString() ?? string.Empty;
                string serial = wmiDisk["SerialNumber"]?.ToString()?.Trim() ?? string.Empty;
                string status = wmiDisk["Status"]?.ToString() ?? "OK";
                long size = 0;
                if (wmiDisk["Size"] != null && long.TryParse(wmiDisk["Size"].ToString(), out long parsedSize))
                {
                    size = parsedSize;
                }

                var physical = new PhysicalDiskInfo
                {
                    DeviceId = deviceId,
                    Model = model,
                    InterfaceType = iface,
                    MediaType = mediaType,
                    SerialNumber = serial,
                    Status = status,
                    SizeBytes = size,
                    SmartPredictFailure = false
                };

                report.PhysicalDisks.Add(physical);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[DiskMonitor] WMI Win32_DiskDrive okuma hatası: {ex.Message}");
        }

        // 3. Try to query SMART failure prediction status (where supported by driver/hardware)
        try
        {
            using var smartSearcher = new ManagementObjectSearcher(
                new ManagementScope(@"\\.\root\wmi"),
                new ObjectQuery("SELECT * FROM MSStorageDriver_FailurePredictStatus")
            );

            foreach (ManagementObject smartObj in smartSearcher.Get())
            {
                string instanceName = smartObj["InstanceName"]?.ToString() ?? string.Empty;
                bool predictFailure = (bool)(smartObj["PredictFailure"] ?? false);

                if (predictFailure)
                {
                    // Find corresponding physical drive or mark first
                    var disk = report.PhysicalDisks.FirstOrDefault(p => instanceName.Contains(p.DeviceId) || instanceName.Contains(p.Model))
                               ?? report.PhysicalDisks.FirstOrDefault();
                    if (disk != null)
                    {
                        disk.SmartPredictFailure = true;
                        disk.Status = "Pred Fail";
                    }
                }
            }
        }
        catch
        {
            // SMART prediction query might not be supported on all RAID/NVMe controllers or requires elevated token, safely ignore
        }

        // 4. Calculate overall status based on thresholds
        report.RecalculateOverallStatus(
            warningThreshold: config.Brevo.WarningThresholdPercent,
            criticalThreshold: config.Brevo.CriticalThresholdPercent
        );

        return report;
    }
}
