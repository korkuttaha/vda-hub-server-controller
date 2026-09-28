using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace VdaHubServerController.Services;

public sealed record PcAgentCycleResult(bool CommandReceived, bool Success, string Message);

public sealed class PcPowerCommandService
{
    private const string Protocol = "1";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };
    private readonly PcConfigService _config;
    private readonly string _statePath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private sealed class PollResponse
    {
        public PcCommand? Command { get; set; }
    }

    private sealed class PcCommand
    {
        public string CommandId { get; set; } = string.Empty;
        public string Action { get; set; } = string.Empty;
        public DateTime ExpiresAtUtc { get; set; }
    }

    private sealed class LocalState
    {
        public string? LastHandledCommandId { get; set; }
        public string? LastAction { get; set; }
        public DateTime? LastHandledAtUtc { get; set; }
    }

    public PcPowerCommandService(PcConfigService config)
    {
        _config = config;
        _statePath = Path.Combine(PcConfigService.InstallDirectory, "command-state.json");
    }

    public async Task<PcAgentCycleResult> PollAndRunAsync(CancellationToken ct = default)
    {
        if (!await _gate.WaitAsync(0, ct))
            return new(false, true, "Kontrol zaten çalışıyor.");

        try
        {
            var settings = _config.Current;
            if (!PcEnrollmentService.IsEnrolled(settings))
                return new(false, false, "PC Agent eşleştirilmemiş.");

            var payload = JsonSerializer.Serialize(new
            {
                computerId = settings.ComputerId,
                machineName = Environment.MachineName,
                osVersion = Environment.OSVersion.VersionString,
                agentVersion = typeof(PcPowerCommandService).Assembly.GetName().Version?.ToString(3) ?? "unknown"
            }, JsonOptions());

            using var request = Authorized(
                HttpMethod.Post,
                new Uri(new Uri(settings.HubBaseUrl), "/api/computers/bridge/poll"),
                settings.ApiKey);
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

            using var response = await Http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                return new(false, false, $"Hub heartbeat reddedildi (HTTP {(int)response.StatusCode}).");

            var body = await response.Content.ReadAsStringAsync(ct);
            var poll = JsonSerializer.Deserialize<PollResponse>(body, JsonOptions());
            var command = poll?.Command;
            if (command is null) return new(false, true, "Heartbeat tamamlandı.");

            if (string.IsNullOrWhiteSpace(command.CommandId) ||
                command.Action is not ("shutdown" or "restart" or "lock") ||
                command.ExpiresAtUtc <= DateTime.UtcNow)
                return new(true, false, "Hub geçersiz veya süresi dolmuş komut döndürdü.");

            var state = LoadState();
            if (string.Equals(state.LastHandledCommandId, command.CommandId, StringComparison.Ordinal))
            {
                await ReportAsync(command.CommandId, true, "Komut daha önce işlendi; tekrar çalıştırılmadı.", ct);
                return new(false, true, "Tekrarlanan komut güvenli biçimde atlandı.");
            }

            SaveState(new LocalState
            {
                LastHandledCommandId = command.CommandId,
                LastAction = command.Action,
                LastHandledAtUtc = DateTime.UtcNow
            });

            try
            {
                Execute(command.Action);
                await ReportAsync(command.CommandId, true, $"{command.Action} komutu işletim sistemine iletildi.", ct);
                return new(true, true, $"{command.Action} komutu işletim sistemine iletildi.");
            }
            catch (Exception ex)
            {
                try
                {
                    await ReportAsync(command.CommandId, false, ex.GetBaseException().Message, ct);
                }
                catch
                {
                    // Local replay state still prevents repeated destructive execution.
                }
                return new(true, false, ex.GetBaseException().Message);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static void Execute(string action)
    {
        switch (action)
        {
            case "shutdown":
                Start("shutdown.exe", "/s /t 2");
                break;
            case "restart":
                Start("shutdown.exe", "/r /t 2");
                break;
            case "lock":
                LockInteractiveSession();
                break;
            default:
                throw new InvalidOperationException("Desteklenmeyen PC komutu.");
        }
    }

    private static void Start(string fileName, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        });
        if (process is null)
            throw new InvalidOperationException("Windows güç işlemi başlatılamadı.");
    }

    private static void LockInteractiveSession()
    {
        var sessionId = WTSGetActiveConsoleSessionId();
        if (sessionId == uint.MaxValue)
            throw new InvalidOperationException("Aktif Windows oturumu bulunamadı.");
        if (!WTSDisconnectSession(IntPtr.Zero, (int)sessionId, false))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows oturumu kilitlenemedi.");
    }

    private async Task ReportAsync(string commandId, bool success, string message, CancellationToken ct)
    {
        var settings = _config.Current;
        var payload = JsonSerializer.Serialize(new
        {
            computerId = settings.ComputerId,
            commandId,
            success,
            message
        }, JsonOptions());

        using var request = Authorized(
            HttpMethod.Post,
            new Uri(new Uri(settings.HubBaseUrl), "/api/computers/bridge/result"),
            settings.ApiKey);
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await Http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Hub sonuç bildirimi reddedildi (HTTP {(int)response.StatusCode}).");
    }

    private LocalState LoadState()
    {
        if (!File.Exists(_statePath)) return new();
        try
        {
            return JsonSerializer.Deserialize<LocalState>(
                File.ReadAllText(_statePath),
                JsonOptions()) ?? new();
        }
        catch
        {
            return new();
        }
    }

    private void SaveState(LocalState state)
    {
        PcConfigService.EnsureProtectedInstallDirectory();
        var temp = _statePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, _statePath, true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private static HttpRequestMessage Authorized(HttpMethod method, Uri uri, string apiKey)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Add("X-API-Key", apiKey);
        request.Headers.Add("X-VDA-PC-Protocol", Protocol);
        return request;
    }

    private static JsonSerializerOptions JsonOptions() => new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSDisconnectSession(IntPtr hServer, int sessionId, bool wait);
}
