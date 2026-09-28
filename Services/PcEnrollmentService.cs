using System.Text;
using System.Text.Json;

namespace VdaHubServerController.Services;

public sealed class PcEnrollmentService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly PcConfigService _config;

    public PcEnrollmentService(PcConfigService config)
    {
        _config = config;
    }

    public static bool IsEnrolled(VdaHubServerController.Models.PcAgentConfig config) =>
        !string.IsNullOrWhiteSpace(config.ComputerId) &&
        config.ApiKey.StartsWith("vda_pc_", StringComparison.Ordinal) &&
        config.ApiKey.Length == "vda_pc_".Length + 64 &&
        Uri.TryCreate(config.HubBaseUrl, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps;

    public string SuggestedHubBaseUrl() =>
        Uri.TryCreate(_config.Current.HubBaseUrl, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps
            ? uri.GetLeftPart(UriPartial.Authority)
            : "https://vddashbrd.runasp.net";

    public async Task<(bool Success, string Message)> EnrollAsync(
        string hubBaseUrl,
        string setupKey,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(hubBaseUrl.Trim(), UriKind.Absolute, out var baseUri) ||
            baseUri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(baseUri.Query) ||
            !string.IsNullOrEmpty(baseUri.Fragment))
            return (false, "Hub adresi geçerli bir HTTPS adresi olmalıdır.");

        setupKey = setupKey.Trim();
        const string prefix = "vda_pc_setup_";
        if (!setupKey.StartsWith(prefix, StringComparison.Ordinal) ||
            setupKey.Length != prefix.Length + 64)
            return (false, "Bilgisayar eşleştirme kodu biçimi geçersiz.");

        var payload = JsonSerializer.Serialize(new
        {
            setupKey,
            machineName = Environment.MachineName,
            osVersion = Environment.OSVersion.VersionString,
            agentVersion = typeof(PcEnrollmentService).Assembly.GetName().Version?.ToString(3) ?? "unknown"
        });

        try
        {
            using var response = await Http.PostAsync(
                new Uri(baseUri, "/api/computers/bridge/enroll"),
                new StringContent(payload, Encoding.UTF8, "application/json"),
                cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                return (false, ReadError(body) ?? $"Hub eşleştirmesi başarısız (HTTP {(int)response.StatusCode}).");

            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var computerId = root.GetProperty("computerId").GetString() ?? string.Empty;
            var computerName = root.GetProperty("computerName").GetString() ?? string.Empty;
            var apiKey = root.GetProperty("apiKey").GetString() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(computerId) ||
                string.IsNullOrWhiteSpace(computerName) ||
                !apiKey.StartsWith("vda_pc_", StringComparison.Ordinal) ||
                apiKey.Length != "vda_pc_".Length + 64)
                return (false, "Hub geçersiz bilgisayar eşleştirme yanıtı döndürdü.");

            _config.Current.ComputerId = computerId;
            _config.Current.ComputerName = computerName;
            _config.Current.HubBaseUrl = baseUri.GetLeftPart(UriPartial.Authority);
            _config.Current.ApiKey = apiKey;
            _config.Save();

            return (true, $"{computerName} VDAKor Hub ile eşleştirildi.");
        }
        catch (TaskCanceledException)
        {
            return (false, "Hub bağlantısı zaman aşımına uğradı.");
        }
        catch (Exception ex)
        {
            return (false, "Hub bağlantısı kurulamadı: " + ex.Message);
        }
    }

    private static string? ReadError(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("error", out var error)
                ? error.GetString()
                : null;
        }
        catch
        {
            return string.IsNullOrWhiteSpace(body) ? null : body;
        }
    }
}
