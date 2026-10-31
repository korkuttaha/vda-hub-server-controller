using System.Text.RegularExpressions;

namespace VdaHubServerController.Models;

public sealed record AgentControlSettings
{
    public long Revision { get; init; } = 1;
    public int WindowsUpdateIntervalMinutes { get; init; } = 60;
    public int ReportIntervalMinutes { get; init; } = 5;
    public string[] ServiceNames { get; init; } = ["VdaMssqlBackup"];

    public bool IsValid() => Revision > 0 && WindowsUpdateIntervalMinutes is >= 60 and <= 360 &&
        ReportIntervalMinutes is >= 1 and <= 10 && ServiceNames is { Length: <= 12 } &&
        ServiceNames.All(name => name is not null && Regex.IsMatch(name, @"\A[A-Za-z0-9_.$ -]{1,80}\z")) &&
        ServiceNames.Distinct(StringComparer.OrdinalIgnoreCase).Count() == ServiceNames.Length;
}

public sealed record AgentDiagnostics
{
    public long AppliedSettingsRevision { get; init; }
    public int WindowsUpdateIntervalMinutes { get; init; } = 60;
    public int ReportIntervalMinutes { get; init; } = 5;
    public DateTime? LastHubSuccessAtUtc { get; init; }
    public DateTime? LastHubAttemptAtUtc { get; init; }
    public string HubStatus { get; init; } = "unknown";
    public DateTime? LastControlSuccessAtUtc { get; init; }
    public string ControlStatus { get; init; } = "unknown";
    public string? ManualScanRequestId { get; init; }
    public string? ManualScanState { get; init; }
}

public sealed record WatchedServiceSnapshot
{
    public string Name { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Status { get; init; } = "error";
    public bool? Installed { get; init; }
    public string State { get; init; } = "unknown";
    public string StartMode { get; init; } = "unknown";
    public DateTime CheckedAtUtc { get; init; }
}
