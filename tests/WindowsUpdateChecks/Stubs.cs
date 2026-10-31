using VdaHubServerController.Models;

namespace VdaHubServerController.Services;

public sealed class ConfigService
{
    public static string InstallDirectory => Path.GetTempPath();
    public AppConfig Current { get; } = new() { Hub = new() { Enabled = true } };
    public void Save() { }
}
public sealed class DiskMonitorService
{
    public ServerStatusReport CollectReport(AppConfig config) => new() { ServerId = config.ServerId };
}
public sealed class HubClientService
{
    public int Reports { get; private set; }
    public Task<(bool, string, int?)> SendReportAsync(HubSettings settings, ServerStatusReport report, CancellationToken ct)
    {
        Reports++;
        return Task.FromResult<(bool, string, int?)>((true, "test", 200));
    }
}
public sealed class BrevoEmailService
{
    public Task<(bool, string)> SendDailyReportAsync(BrevoSettings settings, ServerStatusReport report) => Task.FromResult((true, "test"));
    public Task<(bool, string)> SendCriticalAlertAsync(BrevoSettings settings, ServerStatusReport report, string message) => Task.FromResult((true, "test"));
}
public sealed class BackupService
{
    public Task<(bool Ran, bool Success, string Message)> RunPendingAsync(CancellationToken ct) => Task.FromResult((false, true, "test"));
}
