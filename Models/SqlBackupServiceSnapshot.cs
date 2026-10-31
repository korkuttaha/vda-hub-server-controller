namespace VdaHubServerController.Models;

public sealed record SqlBackupServiceSnapshot
{
    public DateTime CheckedAtUtc { get; init; }
    public string Status { get; init; } = "error";
    public bool? Installed { get; init; }
    public string ServiceState { get; init; } = "unknown";
    public string StartMode { get; init; } = "unknown";
    public string? Version { get; init; }
    public string ScheduleState { get; init; } = "missing";
    public DateTimeOffset? LastAttemptUtc { get; init; }
    public DateTimeOffset? LastCompletedUtc { get; init; }
    public DateTimeOffset? NextRunUtc { get; init; }
    public string LastOutcome { get; init; } = "unknown";
    public bool? InProgress { get; init; }
}
