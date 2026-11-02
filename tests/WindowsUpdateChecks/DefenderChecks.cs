using VdaHubServerController.Models;
using VdaHubServerController.Services;

internal static class DefenderChecks
{
    public static async Task RunAsync(string root)
    {
        var at = new DateTime(2026, 11, 2, 18, 39, 0, DateTimeKind.Utc);
        var signature = new WindowsUpdateItem
        {
            UpdateId = "signature", Title = "Security Intelligence Update - KB2267602 (Version 1.463.94.0)",
            KbArticleIds = ["2267602"], IsSecurity = true
        };
        var snapshot = new WindowsUpdateSnapshot
        {
            Status = "success", PendingCount = 1, SecurityCount = 1, RebootRequired = true,
            CheckedAtUtc = at, LastSuccessfulScanAtUtc = at, Updates = [signature]
        };
        foreach (var version in new[] { "1.463.94.0", "1.463.100.0", "1.464.0.0" })
        {
            var result = DefenderSignatureReconciler.Reconcile(snapshot, version);
            Check(result is { PendingCount: 0, SecurityCount: 0, Updates.Count: 0, RebootRequired: true }, "Installed signature was still pending or erased reboot status.");
            Check(result.CheckedAtUtc == at && result.LastSuccessfulScanAtUtc == at, "Defender reconciliation invented a fresh WUA scan.");
        }
        foreach (var version in new[] { null, "", "garbage", "0.0.0.0", "1.463.94", "1.463.9.0", "1.462.999.0", "1.463.94.0 extra" })
            Check(ReferenceEquals(snapshot, DefenderSignatureReconciler.Reconcile(snapshot, version)), "Unverified or older Defender version hid a pending signature.");
        foreach (var title in new[] { "Microsoft Defender güncelleştirmesi (Sürüm 1.463.94.0)", "Defender (Version 1.463.94.0)" })
            Check(DefenderSignatureReconciler.Reconcile(snapshot with { Updates = [signature with { Title = title }] }, "1.463.94.0").PendingCount == 0, "Localized signature title was not reconciled.");
        foreach (var item in new[]
        {
            signature with { Title = "Defender security intelligence" },
            signature with { Title = "Versions 1.463.94.0 and 1.463.95.0" },
            signature with { Title = "Version 1.463.94.0.1" },
            signature with { Title = "Version 999999999999.1.2.3" },
            signature with { KbArticleIds = [] },
            signature with { KbArticleIds = ["2267602", "4052623"] },
            signature with { KbArticleIds = ["4052623"] }
        })
        {
            var uncertain = snapshot with { Updates = [item] };
            Check(ReferenceEquals(uncertain, DefenderSignatureReconciler.Reconcile(uncertain, "1.463.100.0")), "Ambiguous/unrelated update was hidden.");
        }
        var patch = signature with { UpdateId = "patch", KbArticleIds = ["5000000"], Title = "Cumulative update" };
        var newerSignature = signature with { UpdateId = "newer", Title = "Defender 1.463.95.0", IsSecurity = false };
        var mixed = snapshot with { PendingCount = 4, SecurityCount = 3, Updates = [signature, patch, patch, newerSignature] };
        var mixedResult = DefenderSignatureReconciler.Reconcile(mixed, "1.463.94.0");
        Check(mixedResult is { PendingCount: 3, SecurityCount: 2, Updates.Count: 3 } && mixedResult.Updates.Contains(newerSignature), "Reconciliation removed newer signatures, system patches or duplicate details.");
        var truncated = DefenderSignatureReconciler.Reconcile(snapshot with { PendingCount = 150, UpdatesTruncated = true }, "1.463.94.0");
        Check(truncated is { PendingCount: 149, UpdatesTruncated: true }, "Reconciliation cleared unlisted pending updates.");
        Check(DefenderSignatureReconciler.Reconcile(snapshot with { Status = "error", PendingCount = null, SecurityCount = null }, "1.463.94.0").Status == "error", "Scan error became healthy.");

        var clock = new TestClock();
        var scans = 0;
        var reads = 0;
        var installed = "1.463.93.0";
        var fail = false;
        var cache = Path.Combine(root, "defender.json");
        var monitor = new WindowsUpdateMonitorService(_ => { scans++; return Task.FromResult(snapshot); }, clock, cache,
            _ => { reads++; return fail ? Task.FromException<string?>(new TimeoutException()) : Task.FromResult<string?>(installed); });
        await monitor.RefreshIfDueAsync(default);
        var first = monitor.Current!;
        var settingsWindow = new WindowsUpdateMonitorService(_ => throw new Exception("Must not scan"), clock, cache);
        fail = true;
        clock.Advance(TimeSpan.FromMinutes(1));
        await monitor.RefreshIfDueAsync(default);
        Check(monitor.Current?.PendingCount == 1 && monitor.Current.Status == "success", "Defender read failure suppressed the update or broke the WUA result.");
        fail = false;
        installed = "1.463.94.0";
        clock.Advance(TimeSpan.FromMinutes(1));
        await monitor.RefreshIfDueAsync(default);
        Check(scans == 1 && reads == 3 && monitor.GetSnapshot()?.PendingCount == 0, "Cached signature was not cleared independently of expensive WUA scans.");
        Check(settingsWindow.GetSnapshot()?.PendingCount == 0, "A settings process resurrected the old signature at the same WUA timestamp.");
        var restarted = new WindowsUpdateMonitorService(_ => throw new Exception("Must use cache"), clock, cache);
        Check(restarted.Current?.PendingCount == 0 && restarted.Current.CheckedAtUtc == first.CheckedAtUtc, "Restart resurrected a cleared signature or changed WUA freshness.");
        await monitor.RefreshIfDueAsync(default);
        Check(reads == 3, "Defender was queried without signature candidates.");

        using var stop = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slow = new WindowsUpdateMonitorService(_ => Task.FromResult(snapshot), clock, Path.Combine(root, "slow-defender.json"),
            async ct => { started.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct); return installed; });
        var refresh = slow.RefreshIfDueAsync(stop.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var config = new ConfigService();
        var hub = new HubClientService();
        var control = new ServerAgentControlService(() => config.Current, slow, Path.Combine(root, "defender-control.json"),
            new HttpClient(new ControlHandler(() => new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable))), clock);
        var engine = new ControllerEngine(config, new DiskMonitorService(), new BrevoEmailService(), hub, new BackupService(), slow, control);
        await engine.ExecuteCheckCycleAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Check(hub.Reports == 1, "A blocked Defender provider blocked metrics reporting.");
        stop.Cancel();
        try { await refresh; throw new Exception("Defender cancellation was ignored."); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        Check(slow.Current is null, "Cancellation was recorded as success/failure.");
        Console.WriteLine("PASS: Defender installed/version comparison, conservative fallback, cached reconciliation, restart/shared cache, timestamps and heartbeat isolation.");
    }

    private static void Check(bool valid, string message)
    {
        if (!valid) throw new Exception(message);
    }
}
