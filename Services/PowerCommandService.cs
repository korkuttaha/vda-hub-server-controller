using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using VdaHubServerController.Models;

namespace VdaHubServerController.Services;

public sealed record PowerCommandRunResult(bool Ran, bool Success, string Message);

public sealed class PowerCommandService
{
    private const string PowerProtocol = "1";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly ConfigService _config;
    private readonly string _statePath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private sealed class PowerCommandResponse
    {
        public string CommandId { get; set; } = string.Empty;
        public string Action { get; set; } = string.Empty;
        public DateTime RequestedAtUtc { get; set; }
    }

    private sealed class LocalState
    {
        public string? LastExecutedCommandId { get; set; }
        public string? LastExecutedAction { get; set; }
        public DateTime? LastExecutedAtUtc { get; set; }
    }

    public PowerCommandService(ConfigService config)
    {
        _config = config;
        _statePath = Path.Combine(Path.GetDirectoryName(config.ConfigFilePath)!, "power-command-state.json");
    }

    public async Task<PowerCommandRunResult> RunPendingAsync(CancellationToken ct = default)
    {
        if (!await _gate.WaitAsync(0, ct))
            return new(false, true, "Güç komutu kontrolü zaten çalışıyor.");

        try
        {
            var settings = _config.Current.Hub;
            if (!TryBridgeBase(settings, out var baseUri) ||
                string.IsNullOrWhiteSpace(settings.ApiKey) ||
                string.IsNullOrWhiteSpace(_config.Current.ServerId))
                return new(false, true, "Hub güç komutu bağlantısı yapılandırılmamış.");

            using var request = Authorized(
                HttpMethod.Get,
                new Uri(baseUri, $"/api/server-controller/bridge/power-command?serverId={Uri.EscapeDataString(_config.Current.ServerId)}"),
                settings.ApiKey);
            using var response = await Http.SendAsync(request, ct);
            if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
                return new(false, true, "Bekleyen güç komutu yok.");
            if (!response.IsSuccessStatusCode)
                return new(false, false, $"Hub güç komutu isteğini reddetti (HTTP {(int)response.StatusCode}).");

            var body = await response.Content.ReadAsStringAsync(ct);
            var command = JsonSerializer.Deserialize<PowerCommandResponse>(body, JsonOptions());
            if (command is null ||
                string.IsNullOrWhiteSpace(command.CommandId) ||
                command.Action is not ("shutdown" or "restart" or "lock"))
            {
                if (!string.IsNullOrWhiteSpace(command?.CommandId))
                    await ReportAsync(baseUri, settings.ApiKey, command.CommandId, false, "invalid", "Geçersiz güç komutu.", ct);
                return new(true, false, "Hub geçersiz güç komutu döndürdü.");
            }

            var state = LoadState();
            if (string.Equals(state.LastExecutedCommandId, command.CommandId, StringComparison.Ordinal))
            {
                await ReportAsync(
                    baseUri,
                    settings.ApiKey,
                    command.CommandId,
                    true,
                    "executed",
                    "Komut daha önce işlendi; tekrar çalıştırılmadı.",
                    ct);
                return new(false, true, "Daha önce çalıştırılan güç komutu Hub'a yeniden onaylandı.");
            }

            try
            {
                Execute(command.Action);
                SaveState(new LocalState
                {
                    LastExecutedCommandId = command.CommandId,
                    LastExecutedAction = command.Action,
                    LastExecutedAtUtc = DateTime.UtcNow
                });
            }
            catch (Exception ex)
            {
                await ReportAsync(
                    baseUri,
                    settings.ApiKey,
                    command.CommandId,
                    false,
                    "failed",
                    ex.GetBaseException().Message,
                    ct);
                return new(true, false, $"Güç komutu çalıştırılamadı: {ex.GetBaseException().Message}");
            }

            try
            {
                await ReportAsync(
                    baseUri,
                    settings.ApiKey,
                    command.CommandId,
                    true,
                    "executed",
                    $"{command.Action} komutu işletim sistemine iletildi.",
                    ct);
            }
            catch (Exception)
            {
                // Shutdown/restart ağ bağlantısını sonuç bildirimi tamamlanmadan kesebilir.
                // Yerel command id kaydı sonraki açılışta aynı komutun yeniden çalışmasını önler.
            }

            return new(true, true, $"{command.Action} komutu işletim sistemine iletildi.");
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
                StartWindowsCommand("shutdown.exe", "/s /t 0");
                break;
            case "restart":
                StartWindowsCommand("shutdown.exe", "/r /t 0");
                break;
            case "lock":
                LockInteractiveSession();
                break;
            default:
                throw new InvalidOperationException("Desteklenmeyen güç komutu.");
        }
    }

    private static void StartWindowsCommand(string fileName, string arguments)
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

    private async Task ReportAsync(
        Uri baseUri,
        string apiKey,
        string commandId,
        bool success,
        string status,
        string message,
        CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(new
        {
            serverId = _config.Current.ServerId,
            commandId,
            success,
            status,
            message
        }, JsonOptions());

        using var request = Authorized(
            HttpMethod.Post,
            new Uri(baseUri, "/api/server-controller/bridge/power-result"),
            apiKey);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await Http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Hub güç komutu sonucu reddedildi (HTTP {(int)response.StatusCode}).");
    }

    private LocalState LoadState()
    {
        if (!File.Exists(_statePath)) return new();
        try
        {
            return JsonSerializer.Deserialize<LocalState>(File.ReadAllText(_statePath), JsonOptions()) ?? new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new InvalidDataException("Yerel güç komutu durumu okunamadı.", ex);
        }
    }

    private void SaveState(LocalState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
        var temporary = _statePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, _statePath, true);
    }

    private static bool TryBridgeBase(HubSettings settings, out Uri baseUri)
    {
        baseUri = null!;
        if (!Uri.TryCreate(settings.HubApiUrl, UriKind.Absolute, out var report) ||
            report.Scheme != Uri.UriSchemeHttps)
            return false;
        baseUri = new Uri(report.GetLeftPart(UriPartial.Authority));
        return true;
    }

    private static HttpRequestMessage Authorized(HttpMethod method, Uri uri, string apiKey)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Add("X-API-Key", apiKey);
        request.Headers.Add("X-VDA-Power-Protocol", PowerProtocol);
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
