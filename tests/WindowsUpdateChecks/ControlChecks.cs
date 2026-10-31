using System.Net;
using System.Text;
using System.Text.Json;
using VdaHubServerController.Models;
using VdaHubServerController.Services;

static class ControlChecks
{
    public static async Task RunAsync(string root)
    {
        var clock = new ControlClock();
        var config = new AppConfig { ServerId = "server-a", Hub = new() { Enabled = true, HubApiUrl = "https://hub.example/api/v1/servers/report", ApiKey = "vda_test_secret" } };
        var scans = 0;
        var updates = new WindowsUpdateMonitorService(_ =>
        {
            scans++;
            return Task.FromResult(new WindowsUpdateSnapshot { Status = "success", PendingCount = 0, SecurityCount = 0, RebootRequired = false });
        }, clock, Path.Combine(root, "control-update-cache.json"));
        var settings = new AgentControlSettings { Revision = 2, WindowsUpdateIntervalMinutes = 120, ReportIntervalMinutes = 3, ServiceNames = ["MSSQLSERVER", "MSSQL$INSTANCE", "W3SVC"] };
        object? command = null;
        var wrongServer = false;
        var fail = false;
        string? raw = null;
        var handler = new ControlHandler(() => fail ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : new(HttpStatusCode.OK)
        {
            Content = new StringContent(raw ?? JsonSerializer.Serialize(new { ok = true, serverId = wrongServer ? "other" : config.ServerId, settings, scanRequest = command }), Encoding.UTF8, "application/json")
        });
        var path = Path.Combine(root, "control-state.json");
        var control = new ServerAgentControlService(() => config, updates, path, new HttpClient(handler), clock);
        Check(control.Settings.WindowsUpdateIntervalMinutes == 60 && control.Diagnostics.AppliedSettingsRevision == 0, "Defaults must not claim Hub acknowledgement.");
        await control.PollAsync(default);
        Check(control.Settings.WindowsUpdateIntervalMinutes == 120 && control.Diagnostics.AppliedSettingsRevision == 2, "Hub policy was not applied.");
        Check(handler.LastUri?.AbsolutePath == "/api/server-controller/bridge/agent-control" && handler.LastUri.Query == "?serverId=server-a", "Control used a wrong route/identity.");
        Check(handler.Authorization == "Bearer vda_test_secret", "Control did not authenticate to the configured Hub.");
        Check(!File.ReadAllText(path).Contains("vda_test_secret"), "Control state persisted a credential.");
        await updates.RefreshIfDueAsync(default);
        clock.Advance(TimeSpan.FromMinutes(119));
        await updates.RefreshIfDueAsync(default);
        Check(scans == 1, "Configured two-hour interval was ignored.");
        clock.Advance(TimeSpan.FromMinutes(1));
        await updates.RefreshIfDueAsync(default);
        Check(scans == 2, "Configured two-hour interval did not become due.");
        settings = settings with { Revision = 3, WindowsUpdateIntervalMinutes = 60 };
        var openWindow = new ServerAgentControlService(() => config, updates, path, new HttpClient(handler), clock);
        await control.PollAsync(default);
        openWindow.RecordReportResult(true);
        Check(openWindow.Settings.Revision == 3, "An open settings window did not read the service's newer policy.");
        Check(new ServerAgentControlService(() => config, updates, path, new HttpClient(handler), clock).Settings.Revision == 3,
            "A report from an open settings window overwrote the service's newer policy.");
        clock.Advance(TimeSpan.FromHours(1));
        await updates.RefreshIfDueAsync(default);
        Check(scans == 3, "A changed interval required an agent restart.");
        var requestId = Guid.NewGuid().ToString("D");
        command = new { requestId, requestedAtUtc = clock.GetUtcNow().UtcDateTime, expiresAtUtc = clock.GetUtcNow().AddMinutes(10).UtcDateTime };
        Check(await control.PollAsync(default) && scans == 4 && control.Diagnostics.ManualScanState == "success", "Manual scan did not bypass the schedule.");
        Check(!await control.PollAsync(default) && scans == 4, "Repeated control reply ran the same manual scan twice.");
        var restarted = new ServerAgentControlService(() => config, updates, path, new HttpClient(handler), clock);
        Check(restarted.Settings.WindowsUpdateIntervalMinutes == 60 && restarted.Diagnostics.AppliedSettingsRevision == 3, "Restart lost acknowledged settings.");
        Check(!await restarted.PollAsync(default) && scans == 4, "Restart reran an acknowledged manual request.");
        command = new { requestId = Guid.NewGuid().ToString("D"), requestedAtUtc = clock.GetUtcNow().AddMinutes(-20).UtcDateTime, expiresAtUtc = clock.GetUtcNow().AddMinutes(-10).UtcDateTime };
        Check(!await control.PollAsync(default) && scans == 4, "An expired command was executed.");
        fail = true;
        await control.PollAsync(default);
        Check(control.Settings.Revision == 3 && control.Diagnostics.ControlStatus == "error", "A disconnected Hub discarded working settings.");
        fail = false;
        wrongServer = true;
        settings = settings with { Revision = 4 };
        await control.PollAsync(default);
        Check(control.Settings.Revision == 3, "Another server's settings were applied.");
        wrongServer = false;
        settings = settings with { WindowsUpdateIntervalMinutes = 1 };
        await control.PollAsync(default);
        Check(control.Settings.Revision == 3, "Invalid interval was accepted.");
        settings = settings with { WindowsUpdateIntervalMinutes = 60, ServiceNames = ["bad'; SELECT *"] };
        await control.PollAsync(default);
        Check(control.Settings.Revision == 3, "Invalid service configuration was accepted.");
        settings = settings with { Revision = 2, ServiceNames = [] };
        await control.PollAsync(default);
        Check(control.Settings.Revision == 3, "An older settings revision overwrote a newer one.");
        raw = new string('x', 65537);
        await control.PollAsync(default);
        Check(control.Settings.Revision == 3, "An oversized reply changed policy.");
        control.RecordReportResult(true);
        var lastGood = control.Diagnostics.LastHubSuccessAtUtc;
        clock.Advance(TimeSpan.FromMinutes(5));
        control.RecordReportResult(false);
        Check(control.Diagnostics is { HubStatus: "error" } && control.Diagnostics.LastHubSuccessAtUtc == lastGood, "A report error erased the last successful connection.");
        config.ServerId = "server-b";
        var reenrolled = new ServerAgentControlService(() => config, updates, path, new HttpClient(handler), clock);
        Check(reenrolled.Diagnostics.AppliedSettingsRevision == 0, "Re-enrollment reused another server's acknowledged policy.");
        Console.WriteLine("PASS: central policy validation, live interval change, cache/restart, authenticated route, manual scan deduplication/expiry, outage and diagnostics.");
    }
    private static void Check(bool valid, string message) { if (!valid) throw new Exception(message); }
}

sealed class ControlHandler(Func<HttpResponseMessage> reply) : HttpMessageHandler
{
    public Uri? LastUri { get; private set; }
    public string? Authorization { get; private set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastUri = request.RequestUri;
        Authorization = request.Headers.Authorization?.ToString();
        return Task.FromResult(reply());
    }
}

sealed class ControlClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan elapsed) => _now += elapsed;
}
