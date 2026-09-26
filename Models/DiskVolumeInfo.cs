namespace VdaHubServerController.Models;

public class DiskVolumeInfo
{
    public string Name { get; set; } = string.Empty; // e.g. "C:\"
    public string VolumeLabel { get; set; } = string.Empty; // e.g. "OS", "Data"
    public string DriveType { get; set; } = string.Empty; // e.g. "Fixed"
    public string DriveFormat { get; set; } = string.Empty; // e.g. "NTFS"
    public long TotalSizeBytes { get; set; }
    public long FreeSizeBytes { get; set; }
    public long UsedSizeBytes { get; set; }
    public double UsedPercentage { get; set; }
    public bool IsSystemDrive { get; set; }
    public string HealthStatus { get; set; } = "OK"; // "OK", "WARNING", "CRITICAL"

    // Formatted helper properties
    public double TotalSizeGb => Math.Round(TotalSizeBytes / (1024.0 * 1024 * 1024), 2);
    public double FreeSizeGb => Math.Round(FreeSizeBytes / (1024.0 * 1024 * 1024), 2);
    public double UsedSizeGb => Math.Round(UsedSizeBytes / (1024.0 * 1024 * 1024), 2);
}
