using System.Text.Json;
using VdaHubServerController.Models;
using VdaHubServerController.Services;

var root = Path.Combine(Path.GetTempPath(), "vdakor-update-checks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
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
    clock.Advance(TimeSpan.FromHours(5));
    await monitor.RefreshIfDueAsync(default);
    Check(scans == 1, "Metrics reports caused repeated expensive scans.");
    clock.Advance(TimeSpan.FromHours(1));
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
    var engine = new ControllerEngine(new ConfigService(), new DiskMonitorService(), new BrevoEmailService(), hub, new BackupService(), blocked);
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
    Console.WriteLine("PASS: scheduling, failure/backoff/recovery, restart cache, bounded payload, heartbeat isolation, cancellation, corrupt/malformed cache.");
}
finally
{
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
