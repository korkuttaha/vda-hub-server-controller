using System.Net.Http.Headers;
using System.Text.Json;
using VdaHubServerController.Models;

namespace VdaHubServerController.Services;

// Separate from metrics and backups: an unavailable control endpoint never blocks reporting.
public sealed class ServerAgentControlService
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };
    private readonly Func<AppConfig> _config;
    private readonly WindowsUpdateMonitorService _updates;
    private readonly string _path;
    private readonly HttpClient _http;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private State _state = new();
    private sealed record State
    {
        public string ServerId { get; init; } = "";
        public AgentControlSettings Settings { get; init; } = new();
        public AgentDiagnostics Diagnostics { get; init; } = new();
    }
    private sealed record ControlReply(bool Ok, string ServerId, AgentControlSettings Settings, ScanRequest? ScanRequest);
    private sealed record ScanRequest(string RequestId, DateTime RequestedAtUtc, DateTime ExpiresAtUtc);

    public ServerAgentControlService(Func<AppConfig> config, WindowsUpdateMonitorService updates, string path,
        HttpClient? http = null, TimeProvider? clock = null)
    {
        _config = config;
        _updates = updates;
        _path = path;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _clock = clock ?? TimeProvider.System;
        _state = new State { ServerId = config().ServerId };
        try
        {
            if (File.Exists(path) && new FileInfo(path).Length <= 64 * 1024)
            {
                var saved = JsonSerializer.Deserialize<State>(File.ReadAllText(path), Json);
                if (saved?.Settings?.IsValid() == true && saved.Diagnostics is not null && saved.ServerId == config().ServerId) _state = saved;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
        _updates.SetIntervalMinutes(_state.Settings.WindowsUpdateIntervalMinutes);
    }

    public AgentControlSettings Settings { get { lock (_gate) { RefreshSaved(); return _state.Settings with { ServiceNames = [.. _state.Settings.ServiceNames] }; } } }
    public AgentDiagnostics Diagnostics { get { lock (_gate) { RefreshSaved(); return _state.Diagnostics; } } }

    private void RefreshSaved()
    {
        if (_state.ServerId != _config().ServerId)
        {
            _state = new State { ServerId = _config().ServerId };
            _updates.SetIntervalMinutes(60);
        }
        try
        {
            if (!File.Exists(_path) || new FileInfo(_path).Length > 64 * 1024) return;
            var saved = JsonSerializer.Deserialize<State>(File.ReadAllText(_path), Json);
            if (saved?.ServerId != _config().ServerId || saved.Settings?.IsValid() != true || saved.Diagnostics is null ||
                saved.Settings.Revision < _state.Settings.Revision) return;
            var newer = saved.Settings.Revision > _state.Settings.Revision ||
                (saved.Diagnostics.LastControlSuccessAtUtc ?? DateTime.MinValue) > (_state.Diagnostics.LastControlSuccessAtUtc ?? DateTime.MinValue) ||
                saved.Diagnostics.ManualScanRequestId == _state.Diagnostics.ManualScanRequestId &&
                _state.Diagnostics.ManualScanState == "running" && saved.Diagnostics.ManualScanState is "success" or "error";
            if (!newer) return;
            var diagnostics = saved.Diagnostics;
            if (_state.Diagnostics.LastHubAttemptAtUtc > diagnostics.LastHubAttemptAtUtc ||
                _state.Diagnostics.LastHubAttemptAtUtc.HasValue && !diagnostics.LastHubAttemptAtUtc.HasValue)
                diagnostics = diagnostics with
                {
                    LastHubAttemptAtUtc = _state.Diagnostics.LastHubAttemptAtUtc,
                    LastHubSuccessAtUtc = _state.Diagnostics.LastHubSuccessAtUtc,
                    HubStatus = _state.Diagnostics.HubStatus
                };
            _state = saved with { Diagnostics = diagnostics };
            _updates.SetIntervalMinutes(saved.Settings.WindowsUpdateIntervalMinutes);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
    }

    public void RecordReportResult(bool success)
    {
        lock (_gate)
        {
            var now = _clock.GetUtcNow().UtcDateTime;
            _state = _state with
            {
                Diagnostics = _state.Diagnostics with
                {
                    LastHubAttemptAtUtc = now,
                    HubStatus = success ? "success" : "error",
                    LastHubSuccessAtUtc = success ? now : _state.Diagnostics.LastHubSuccessAtUtc
                }
            };
            // Only the control loop persists settings/command state. A simultaneous
            // settings window must never overwrite the service's newer policy.
        }
    }

    public async Task<bool> PollAsync(CancellationToken ct)
    {
        var config = _config();
        if (!config.Hub.Enabled) return false;
        try
        {
            if (!Uri.TryCreate(config.Hub.HubApiUrl, UriKind.Absolute, out var hub) || hub.Scheme != Uri.UriSchemeHttps)
                throw new InvalidDataException("Invalid Hub endpoint.");
            var url = new Uri(hub, "/api/server-controller/bridge/agent-control?serverId=" + Uri.EscapeDataString(config.ServerId));
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.Hub.ApiKey);
            using var networkTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            networkTimeout.CancelAfter(TimeSpan.FromSeconds(15));
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, networkTimeout.Token);
            response.EnsureSuccessStatusCode();
            using var stream = await response.Content.ReadAsStreamAsync(networkTimeout.Token);
            using var buffer = new MemoryStream();
            var chunk = new byte[4096];
            int count;
            while ((count = await stream.ReadAsync(chunk, networkTimeout.Token)) > 0)
            {
                if (buffer.Length + count > 64 * 1024) throw new InvalidDataException("Control reply too large.");
                buffer.Write(chunk, 0, count);
            }
            var reply = JsonSerializer.Deserialize<ControlReply>(buffer.ToArray(), Json);
            if (reply is not { Ok: true } || reply.ServerId != config.ServerId || reply.Settings?.IsValid() != true)
                throw new InvalidDataException("Invalid control reply.");
            var now = _clock.GetUtcNow().UtcDateTime;
            bool scan;
            lock (_gate)
            {
                RefreshSaved();
                if (_config().ServerId != config.ServerId) return false;
                if (reply.Settings.Revision < _state.Diagnostics.AppliedSettingsRevision)
                    throw new InvalidDataException("Stale control reply.");
                var command = reply.ScanRequest;
                scan = command is not null && Guid.TryParseExact(command.RequestId, "D", out _) &&
                    command.RequestedAtUtc <= now.AddMinutes(1) && command.ExpiresAtUtc > now &&
                    command.ExpiresAtUtc > command.RequestedAtUtc && command.ExpiresAtUtc - command.RequestedAtUtc <= TimeSpan.FromMinutes(10) &&
                    command.RequestId != _state.Diagnostics.ManualScanRequestId;
                var next = new State
                {
                    ServerId = config.ServerId,
                    Settings = reply.Settings,
                    Diagnostics = _state.Diagnostics with
                    {
                        AppliedSettingsRevision = reply.Settings.Revision,
                        WindowsUpdateIntervalMinutes = reply.Settings.WindowsUpdateIntervalMinutes,
                        ReportIntervalMinutes = reply.Settings.ReportIntervalMinutes,
                        LastControlSuccessAtUtc = now,
                        ControlStatus = "success",
                        ManualScanRequestId = scan ? command!.RequestId : _state.Diagnostics.ManualScanRequestId,
                        ManualScanState = scan ? "running" : _state.Diagnostics.ManualScanState
                    }
                };
                // Persist before acknowledgement/execution. Repeated replies or restarts do not rerun a command.
                Persist(next);
                _state = next;
                _updates.SetIntervalMinutes(next.Settings.WindowsUpdateIntervalMinutes);
            }
            if (!scan) return false;
            await _updates.ScanNowAsync(ct);
            lock (_gate)
            {
                _state = _state with
                {
                    Diagnostics = _state.Diagnostics with
                    {
                        ManualScanState = _updates.Current?.Status == "success" ? "success" : "error"
                    }
                };
                Persist(_state);
            }
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException or JsonException or
            TaskCanceledException or InvalidOperationException or ArgumentException)
        {
            lock (_gate) _state = _state with { Diagnostics = _state.Diagnostics with { ControlStatus = "error" } };
            return false;
        }
    }

    private void Persist(State next)
    {
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            File.WriteAllText(temporary, JsonSerializer.Serialize(next, Json));
            File.Move(temporary, _path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
