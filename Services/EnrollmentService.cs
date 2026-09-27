using System.Text;
using System.Text.Json;
using VdaHubServerController.Models;

namespace VdaHubServerController.Services;

public sealed record AgentEnrollmentResult(
    string ServerId,
    string ServerName,
    string HubApiUrl,
    string ApiKey);

public sealed class EnrollmentService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly ConfigService _config;

    public EnrollmentService(ConfigService config)
    {
        _config = config;
    }

    public static bool IsEnrolled(AppConfig config) =>
        !string.IsNullOrWhiteSpace(config.ServerId) &&
        config.Hub.ApiKey.StartsWith("vda_", StringComparison.Ordinal) &&
        config.Hub.ApiKey.Length == 68 &&
        Uri.TryCreate(config.Hub.HubApiUrl, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps;

    public string SuggestedHubBaseUrl()
    {
        if (Uri.TryCreate(_config.Current.Hub.HubApiUrl, UriKind.Absolute, out var current) &&
            current.Scheme == Uri.UriSchemeHttps &&
            !current.Host.Equals("your-vda-hub.com", StringComparison.OrdinalIgnoreCase))
        {
            return current.GetLeftPart(UriPartial.Authority);
        }

        return "https://vddashbrd.runasp.net";
    }

    public async Task<(bool Success, string Message)> EnrollAsync(
        string hubBaseUrl,
        string setupKey,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(hubBaseUrl.Trim(), UriKind.Absolute, out var baseUri) ||
            baseUri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(baseUri.Query) ||
            !string.IsNullOrEmpty(baseUri.Fragment))
        {
            return (false, "Hub adresi geçerli bir HTTPS adresi olmalıdır.");
        }

        setupKey = setupKey.Trim();
        if (!setupKey.StartsWith("vda_setup_", StringComparison.Ordinal) || setupKey.Length != 74)
            return (false, "Kurulum anahtarı biçimi geçersiz.");

        var endpoint = new Uri(baseUri, "/api/server-controller/bridge/enroll");
        var payload = JsonSerializer.Serialize(new
        {
            setupKey,
            machineName = Environment.MachineName,
            osVersion = Environment.OSVersion.VersionString
        });

        try
        {
            using var response = await Http.PostAsync(
                endpoint,
                new StringContent(payload, Encoding.UTF8, "application/json"),
                cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                string message;
                try
                {
                    using var error = JsonDocument.Parse(body);
                    message = error.RootElement.TryGetProperty("error", out var element)
                        ? element.GetString() ?? body
                        : body;
                }
                catch
                {
                    message = body;
                }

                return (false, string.IsNullOrWhiteSpace(message)
                    ? $"Hub eşleştirmesi başarısız (HTTP {(int)response.StatusCode})."
                    : message);
            }

            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var result = new AgentEnrollmentResult(
                root.GetProperty("serverId").GetString() ?? string.Empty,
                root.GetProperty("serverName").GetString() ?? string.Empty,
                root.GetProperty("hubApiUrl").GetString() ?? string.Empty,
                root.GetProperty("apiKey").GetString() ?? string.Empty);

            if (string.IsNullOrWhiteSpace(result.ServerId) ||
                string.IsNullOrWhiteSpace(result.ServerName) ||
                !result.ApiKey.StartsWith("vda_", StringComparison.Ordinal) ||
                result.ApiKey.Length != 68 ||
                !Uri.TryCreate(result.HubApiUrl, UriKind.Absolute, out var reportUri) ||
                reportUri.Scheme != Uri.UriSchemeHttps)
            {
                return (false, "Hub geçersiz eşleştirme yanıtı döndürdü.");
            }

            var config = _config.Current;
            config.ServerId = result.ServerId;
            config.ServerName = result.ServerName;
            config.Hub.Enabled = true;
            config.Hub.HubApiUrl = result.HubApiUrl;
            config.Hub.ApiKey = result.ApiKey;
            _config.Save();

            return (true, $"{result.ServerName} sunucusu Hub ile eşleştirildi.");
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
}
