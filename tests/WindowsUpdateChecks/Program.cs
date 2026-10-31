using System.Text.Json;
using VdaHubServerController.Models;
using VdaHubServerController.Services;

var root = Path.Combine(Path.GetTempPath(), "vdakor-update-checks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    await ControlChecks.RunAsync(root);
    var sqlSnapshot = new SqlBackupServiceSnapshot { Status = "success", Installed = true, CheckedAtUtc = DateTime.UtcNow };
    Check(SqlBackupStateReader.Read(sqlSnapshot, root).ScheduleState == "missing", "Missing SQL scheduler state must be distinct from success.");
    var sqlStateDirectory = Path.Combine(root, "VDA.MssqlBackup.Service", "state");
    Directory.CreateDirectory(sqlStateDirectory);
    var sqlStatePath = Path.Combine(sqlStateDirectory, "schedule-state.json");
    File.WriteAllText(sqlStatePath, JsonSerializer.Serialize(new
    {
        NextRunUtc = DateTimeOffset.UtcNow.AddHours(1), LastAttemptUtc = DateTimeOffset.UtcNow.AddMinutes(-5),
        LastCompletedUtc = DateTimeOffset.UtcNow.AddMinutes(-1), LastOutcome = "Başarılı", InProgress = false,
        Password = "must-never-leave-the-server", ConnectionString = "private-connection", LastOperationId = "private-operation"
    }));
    var sqlState = SqlBackupStateReader.Read(sqlSnapshot, root);
    Check(sqlState is { ScheduleState: "available", LastOutcome: "succeeded", InProgress: false, LastCompletedUtc: not null }, "SQL scheduler success lost details.");
    var sqlJson = JsonSerializer.Serialize(sqlState);
    Check(!sqlJson.Contains("must-never") && !sqlJson.Contains("private-"), "Private SQL state was included in the report.");
    File.WriteAllText(sqlStatePath, "{broken");
    Check(SqlBackupStateReader.Read(sqlSnapshot, root) is { Installed: true, ScheduleState: "error" }, "A corrupt SQL state erased installed service status.");
    File.WriteAllText(sqlStatePath, new string('x', 65537));
    Check(SqlBackupStateReader.Read(sqlSnapshot, root).ScheduleState == "error", "Oversized SQL state was accepted.");
    File.WriteAllText(sqlStatePath, JsonSerializer.Serialize(new
    {
        NextRunUtc = DateTimeOffset.UtcNow.AddHours(1), LastOutcome = "Password=must-never-leave-the-server",
        LastCompletedUtc = DateTimeOffset.UtcNow.AddDays(1), InProgress = "invalid"
    }));
    Check(SqlBackupStateReader.Read(sqlSnapshot, root) is { ScheduleState: "available", LastOutcome: "unknown", LastCompletedUtc: null, InProgress: null },
        "Unknown outcomes or invalid timestamps/booleans leaked into SQL status.");

    var clock = new TestClock();
    var scans = 0;
    var fail = false;
    var cache = Path.Combine(root, "cache.json");
    var monitor = new WindowsUpdateMonitorService(ct =>
    {
        scans++;
        return fail ? Task.FromException<WindowsUpdateSnapshot>(new TimeoutException()) : Task.FromResult(Success(3, true));
    }, clock, cache);
    Check(monitor.Current is null, "A new agent must initially be unknown.");
    await monitor.RefreshIfDueAsync(default);
    Check(monitor.Current is { Status: "success", PendingCount: 3, RebootRequired: true }, "Successful scan lost state.");
    var lastGood = monitor.Current!.LastSuccessfulScanAtUtc;
    clock.Advance(TimeSpan.FromMinutes(59));
    await monitor.RefreshIfDueAsync(default);
    Check(scans == 1, "Metrics reports caused repeated expensive scans.");
    clock.Advance(TimeSpan.FromMinutes(1));
    fail = true;
    await monitor.RefreshIfDueAsync(default);
    Check(monitor.Current is { Status: "error", PendingCount: null, RebootRequired: null }, "Failure was reported as healthy/cached zero.");
    Check(monitor.Current!.LastSuccessfulScanAtUtc == lastGood, "Failure lost last successful scan time.");
    await monitor.RefreshIfDueAsync(default);
    Check(scans == 2, "Failed scanner retried without backoff.");
    clock.Advance(TimeSpan.FromHours(1));
    fail = false;
    await monitor.RefreshIfDueAsync(default);
    Check(scans == 3 && monitor.Current!.Status == "success", "Failure did not recover after backoff.");
    var restarted = new WindowsUpdateMonitorService(_ => throw new Exception("Should use cache"), clock, cache);
    await restarted.RefreshIfDueAsync(default);
    Check(restarted.Current?.PendingCount == 3, "Restart discarded a fresh successful cache.");

    var many = new WindowsUpdateMonitorService(_ => Task.FromResult(Success(150, false) with
    {
        Updates = Enumerable.Range(0, 150).Select(i => new WindowsUpdateItem
        {
            UpdateId = i.ToString(), Title = new string('ğ', 300), KbArticleIds = Enumerable.Repeat(new string('9', 24), 8).ToList()
        }).ToList()
    }), clock, Path.Combine(root, "many.json"));
    await many.RefreshIfDueAsync(default);
    Check(many.Current is { PendingCount: 150, UpdatesTruncated: true }, "Truncated detail changed full pending count.");
    Check(JsonSerializer.SerializeToUtf8Bytes(many.Current).Length < 100 * 1024, "Update data exceeded the payload budget.");

    using var stop = new CancellationTokenSource();
    var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var blocked = new WindowsUpdateMonitorService(async ct =>
    {
        started.SetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        return Success(0, false);
    }, clock, Path.Combine(root, "blocked.json"));
    var hub = new HubClientService();
    var testConfig = new ConfigService();
    var testControl = new ServerAgentControlService(() => testConfig.Current, blocked, Path.Combine(root, "engine-control.json"),
        new HttpClient(new ControlHandler(() => new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable))), clock);
    var engine = new ControllerEngine(testConfig, new DiskMonitorService(), new BrevoEmailService(), hub, new BackupService(), blocked, testControl);
    var loop = engine.StartLoopAsync(stop.Token);
    await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
    Check(hub.Reports == 1, "A blocked Windows Update scan blocked the initial metrics report.");
    stop.Cancel();
    await loop.WaitAsync(TimeSpan.FromSeconds(2));
    Check(blocked.Current is null, "Service cancellation was recorded as an update failure.");

    File.WriteAllText(Path.Combine(root, "corrupt.json"), "{bad json}");
    var corrupt = new WindowsUpdateMonitorService(_ => Task.FromResult(Success(0, false)), clock, Path.Combine(root, "corrupt.json"));
    Check(corrupt.Current is null, "Corrupt cache was trusted.");
    await corrupt.RefreshIfDueAsync(default);
    Check(corrupt.Current is { Status: "success", PendingCount: 0 }, "Corrupt cache prevented a fresh scan.");
    foreach (var invalid in new[] { "{}", "{\"pendingCount\":-1,\"securityCount\":0,\"rebootRequired\":false}", "{\"pendingCount\":0,\"securityCount\":1,\"rebootRequired\":false}" })
    {
        var name = Path.Combine(root, "invalid.json");
        var fields = invalid.Trim('{', '}');
        File.WriteAllText(name, "{\"status\":\"success\",\"checkedAtUtc\":\"" + clock.GetUtcNow().ToString("o") + "\"" + (fields.Length > 0 ? "," + fields : "") + "}");
        var invalidCache = new WindowsUpdateMonitorService(_ => Task.FromResult(Success(0, false)), clock, name);
        Check(invalidCache.Current is null, "Malformed successful cache suppressed a real scan.");
    }
    var nullItemsPath = Path.Combine(root, "null-items.json");
    File.WriteAllText(nullItemsPath, JsonSerializer.Serialize(Success(1, false) with { CheckedAtUtc = clock.GetUtcNow().UtcDateTime, Updates = [null!] }));
    var nullItems = new WindowsUpdateMonitorService(_ => Task.FromResult(Success(0, false)), clock, nullItemsPath);
    Check(nullItems.Current is { PendingCount: 1, Updates.Count: 0 }, "Null detail crashed cache loading.");
    var sharedPath = Path.Combine(root, "shared.json");
    var openSettings = new WindowsUpdateMonitorService(_ => throw new Exception("Settings must not scan"), clock, sharedPath);
    var serviceMonitor = new WindowsUpdateMonitorService(_ => Task.FromResult(Success(4, true)), clock, sharedPath);
    await serviceMonitor.RefreshIfDueAsync(default);
    Check(openSettings.GetSnapshot() is { PendingCount: 4, RebootRequired: true }, "Open settings window failed to read the service's newer scan.");
    Console.WriteLine("PASS: SQL backup state allowlist, size bounds, missing/corrupt data; Windows Update scheduling, recovery, cache, heartbeat isolation and cancellation.");
}
finally
{
    var sqlDirectory = Path.Combine(root, "VDA.MssqlBackup.Service");
    var sqlStateDirectory = Path.Combine(sqlDirectory, "state");
    var sqlStatePath = Path.Combine(sqlStateDirectory, "schedule-state.json");
    if (File.Exists(sqlStatePath)) File.Delete(sqlStatePath);
    if (Directory.Exists(sqlStateDirectory)) Directory.Delete(sqlStateDirectory);
    if (Directory.Exists(sqlDirectory)) Directory.Delete(sqlDirectory);
    // Every file is generated inside this unique, fixed temporary test directory.
    foreach (var file in Directory.EnumerateFiles(root)) File.Delete(file);
    Directory.Delete(root);
}

static WindowsUpdateSnapshot Success(int count, bool reboot) => new()
{
    Status = "success", PendingCount = count, SecurityCount = Math.Min(1, count), RebootRequired = reboot
};
static void Check(bool valid, string message)
{
    if (!valid) throw new Exception(message);
}
sealed class TestClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan elapsed) => _now += elapsed;
}
