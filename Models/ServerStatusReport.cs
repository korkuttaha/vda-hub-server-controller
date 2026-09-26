namespace VdaHubServerController.Models;

public class ServerStatusReport
{
    public string ServerId { get; set; } = string.Empty;
    public string ServerName { get; set; } = string.Empty;
    public string MachineName { get; set; } = Environment.MachineName;
    public string OsVersion { get; set; } = Environment.OSVersion.ToString();
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string OverallStatus { get; set; } = "HEALTHY"; // "HEALTHY", "WARNING", "CRITICAL"
    public string SummaryMessage { get; set; } = string.Empty;

    // CPU & RAM metrics
    public double? CpuUsagePercent { get; set; }
    public double? RamTotalGb { get; set; }
    public double? RamUsedGb { get; set; }
    public double? RamFreeGb { get; set; }
    public double? RamUsagePercent { get; set; }
    public string? PublicIp { get; set; }

    public List<DiskVolumeInfo> Volumes { get; set; } = new();
    public List<PhysicalDiskInfo> PhysicalDisks { get; set; } = new();

    // Summary calculation
    public void RecalculateOverallStatus(int warningThreshold = 80, int criticalThreshold = 90)
    {
        bool hasCritical = false;
        bool hasWarning = false;
        var issues = new List<string>();

        foreach (var vol in Volumes)
        {
            if (vol.UsedPercentage >= criticalThreshold)
            {
                vol.HealthStatus = "CRITICAL";
                hasCritical = true;
                issues.Add($"{vol.Name} disk doluluğu %{vol.UsedPercentage:F1} (Kritik)");
            }
            else if (vol.UsedPercentage >= warningThreshold)
            {
                vol.HealthStatus = "WARNING";
                hasWarning = true;
                issues.Add($"{vol.Name} disk doluluğu %{vol.UsedPercentage:F1} (Uyarı)");
            }
            else
            {
                vol.HealthStatus = "OK";
            }
        }

        foreach (var phys in PhysicalDisks)
        {
            if (phys.SmartPredictFailure || phys.Status.Equals("Pred Fail", StringComparison.OrdinalIgnoreCase) || phys.Status.Equals("Error", StringComparison.OrdinalIgnoreCase))
            {
                hasCritical = true;
                issues.Add($"{phys.Model} ({phys.DeviceId}) SMART arıza tahmini/hatası!");
            }
            else if (phys.Status.Equals("Degraded", StringComparison.OrdinalIgnoreCase))
            {
                hasWarning = true;
                issues.Add($"{phys.Model} ({phys.DeviceId}) disk performansı düştü (Degraded)");
            }
        }

        if (hasCritical)
        {
            OverallStatus = "CRITICAL";
            SummaryMessage = string.Join("; ", issues);
        }
        else if (hasWarning)
        {
            OverallStatus = "WARNING";
            SummaryMessage = string.Join("; ", issues);
        }
        else
        {
            OverallStatus = "HEALTHY";
            SummaryMessage = "Tüm diskler ve sağlık durumları normal.";
        }
    }
}
