namespace VdaHubServerController.Models;

public class PhysicalDiskInfo
{
    public string DeviceId { get; set; } = string.Empty; // e.g. "\\\\.\\PHYSICALDRIVE0"
    public string Model { get; set; } = string.Empty; // e.g. "Samsung SSD 980 PRO 1TB"
    public string InterfaceType { get; set; } = string.Empty; // "SCSI", "NVMe", "IDE"
    public string MediaType { get; set; } = string.Empty; // "Fixed hard disk media", "SSD"
    public long SizeBytes { get; set; }
    public string Status { get; set; } = "OK"; // "OK", "Degraded", "Pred Fail", "Error"
    public string SerialNumber { get; set; } = string.Empty;
    public bool SmartPredictFailure { get; set; }
    public int? TemperatureCelsius { get; set; }

    public double SizeGb => Math.Round(SizeBytes / (1024.0 * 1024 * 1024), 2);
}
