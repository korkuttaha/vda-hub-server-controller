using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using VdaHubServerController.Models;

namespace VdaHubServerController.Services;

public class HubClientService
{
    private static readonly HttpClient HttpClient = new();

    public async Task<(bool Success, string Message, int? StatusCode)> SendReportAsync(
        HubSettings hubSettings,
        ServerStatusReport report,
        CancellationToken ct = default)
    {
        if (!hubSettings.Enabled)
        {
            return (false, "VDA Hub gönderimi ayarlardan devre dışı bırakılmış.", null);
        }

        if (string.IsNullOrWhiteSpace(hubSettings.HubApiUrl))
        {
            return (false, "VDA Hub API URL adresi tanımlanmamış.", null);
        }

        try
        {
            string json = JsonSerializer.Serialize(report, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = false
            });

            using var request = new HttpRequestMessage(HttpMethod.Post, hubSettings.HubApiUrl.Trim());
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");

            if (!string.IsNullOrWhiteSpace(hubSettings.ApiKey))
            {
                request.Headers.Add("X-API-Key", hubSettings.ApiKey.Trim());
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", hubSettings.ApiKey.Trim());
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, hubSettings.TimeoutSeconds)));

            using var response = await HttpClient.SendAsync(request, cts.Token);
            string responseBody = await response.Content.ReadAsStringAsync(cts.Token);

            if (response.IsSuccessStatusCode)
            {
                return (true, "Rapor VDA Hub'a başarıyla iletildi.", (int)response.StatusCode);
            }
            else
            {
                return (false, $"Hub API Hatası (HTTP {(int)response.StatusCode}): {responseBody}", (int)response.StatusCode);
            }
        }
        catch (TaskCanceledException)
        {
            return (false, "VDA Hub sunucusuna bağlanırken zaman aşımı (timeout) oluştu.", null);
        }
        catch (Exception ex)
        {
            return (false, $"VDA Hub bağlantı hatası: {ex.Message}", null);
        }
    }
}
