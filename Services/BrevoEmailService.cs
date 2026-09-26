using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using VdaHubServerController.Models;

namespace VdaHubServerController.Services;

public class BrevoEmailService
{
    private static readonly HttpClient HttpClient = new();
    private const string BrevoApiEndpoint = "https://api.brevo.com/v3/smtp/email";

    public async Task<(bool Success, string Message)> SendEmailAsync(
        BrevoSettings settings,
        string subject,
        string htmlBody,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            return (false, "Brevo API Key tanımlanmamış.");
        }

        if (string.IsNullOrWhiteSpace(settings.SenderEmail))
        {
            return (false, "Gönderen e-posta (Sender Email) tanımlanmamış.");
        }

        if (string.IsNullOrWhiteSpace(settings.RecipientEmails))
        {
            return (false, "Alıcı e-posta (Recipient Email) tanımlanmamış.");
        }

        var recipients = settings.RecipientEmails
            .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(email => new { email = email.Trim() })
            .ToList();

        if (recipients.Count == 0)
        {
            return (false, "Geçerli bir alıcı e-posta adresi bulunamadı.");
        }

        var payload = new
        {
            sender = new
            {
                name = string.IsNullOrWhiteSpace(settings.SenderName) ? "VDA Hub Controller" : settings.SenderName,
                email = settings.SenderEmail.Trim()
            },
            to = recipients,
            subject = subject,
            htmlContent = htmlBody
        };

        string jsonPayload = JsonSerializer.Serialize(payload);

        using var request = new HttpRequestMessage(HttpMethod.Post, BrevoApiEndpoint);
        request.Headers.Add("api-key", settings.ApiKey.Trim());
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

        try
        {
            using var response = await HttpClient.SendAsync(request, ct);
            string responseBody = await response.Content.ReadAsStringAsync(ct);

            if (response.IsSuccessStatusCode)
            {
                return (true, "E-posta Brevo üzerinden başarıyla gönderildi.");
            }
            else
            {
                return (false, $"Brevo API Hatası ({response.StatusCode}): {responseBody}");
            }
        }
        catch (Exception ex)
        {
            return (false, $"Brevo bağlantı hatası: {ex.Message}");
        }
    }

    public async Task<(bool Success, string Message)> SendTestEmailAsync(BrevoSettings settings, string serverName)
    {
        string subject = $"[VDA Hub] Brevo Test E-Postası - {serverName}";
        string body = $@"
<!DOCTYPE html>
<html>
<head>
<meta charset='utf-8'>
<style>
  body {{ font-family: 'Segoe UI', Tahoma, Geneva, Verdana, sans-serif; background-color: #f4f6f9; margin: 0; padding: 20px; }}
  .card {{ max-width: 600px; margin: 0 auto; background: #ffffff; border-radius: 8px; box-shadow: 0 4px 6px rgba(0,0,0,0.1); overflow: hidden; }}
  .header {{ background: #2563eb; color: #ffffff; padding: 24px; text-align: center; }}
  .content {{ padding: 24px; color: #1e293b; line-height: 1.6; }}
  .badge {{ display: inline-block; padding: 6px 12px; background: #10b981; color: #fff; font-weight: bold; border-radius: 4px; }}
  .footer {{ background: #f8fafc; padding: 16px; text-align: center; font-size: 12px; color: #64748b; }}
</style>
</head>
<body>
  <div class='card'>
    <div class='header'>
      <h2 style='margin:0;'>VDA Hub Server Controller</h2>
      <p style='margin:4px 0 0 0; opacity:0.9;'>Brevo Entegrasyon Test Bildirimi</p>
    </div>
    <div class='content'>
      <p>Merhaba,</p>
      <p>Bu e-posta <strong>{serverName}</strong> sunucusundaki <strong>VDA Hub Server Controller</strong> uygulamasından Brevo API bağlantısını doğrulamak amacıyla gönderilmiştir.</p>
      <p><span class='badge'>✓ Brevo API Bağlantısı Başarılı</span></p>
      <p>Günlük HDD durumu raporları ve kritik doluluk bildirimleri bu yapılandırma üzerinden iletilecektir.</p>
    </div>
    <div class='footer'>
      VDA Hub Server Controller • {DateTime.Now:yyyy-MM-dd HH:mm:ss}
    </div>
  </div>
</body>
</html>";

        return await SendEmailAsync(settings, subject, body);
    }

    public async Task<(bool Success, string Message)> SendDailyReportAsync(BrevoSettings settings, ServerStatusReport report)
    {
        string statusLabel = report.OverallStatus switch
        {
            "CRITICAL" => "KRİTİK UYARI",
            "WARNING" => "DİKKAT (YÜKSEK DOLULUK)",
            _ => "SAĞLIKLI"
        };

        string subject = $"[VDA Hub] Günlük HDD Durum Raporu: {report.ServerName} ({statusLabel})";
        string html = GenerateHtmlReport(report, isDailyReport: true);

        return await SendEmailAsync(settings, subject, html);
    }

    public async Task<(bool Success, string Message)> SendCriticalAlertAsync(BrevoSettings settings, ServerStatusReport report, string alertReason)
    {
        string subject = $"[VDA Hub ALARM] {report.ServerName} Sunucusunda Kritik Disk Uyarısı!";
        string html = GenerateHtmlReport(report, isDailyReport: false, extraAlertMessage: alertReason);

        return await SendEmailAsync(settings, subject, html);
    }

    private static string GenerateHtmlReport(ServerStatusReport report, bool isDailyReport, string? extraAlertMessage = null)
    {
        string badgeBg = report.OverallStatus switch
        {
            "CRITICAL" => "#ef4444",
            "WARNING" => "#f59e0b",
            _ => "#10b981"
        };

        string statusText = report.OverallStatus switch
        {
            "CRITICAL" => "⛔ KRİTİK SEVİYE",
            "WARNING" => "⚠ DİKKAT GEREKİYOR",
            _ => "✓ TÜM DİSKLER SAĞLIKLI"
        };

        var sbVolumes = new StringBuilder();
        foreach (var vol in report.Volumes)
        {
            string barColor = vol.UsedPercentage >= 90 ? "#ef4444" : (vol.UsedPercentage >= 80 ? "#f59e0b" : "#10b981");

            sbVolumes.Append($@"
            <div style='margin-bottom: 16px; padding: 12px; border: 1px solid #e2e8f0; border-radius: 6px; background-color: #f8fafc;'>
              <div style='display: flex; justify-content: space-between; font-weight: 600; margin-bottom: 6px;'>
                <span>{vol.Name} ({vol.VolumeLabel}) {(vol.IsSystemDrive ? "<small style='color:#6366f1;'>[Sistem]</small>" : "")}</span>
                <span style='color: {barColor};'>%{vol.UsedPercentage:F1} Dolu</span>
              </div>
              <div style='background-color: #e2e8f0; border-radius: 4px; height: 14px; overflow: hidden; margin-bottom: 6px;'>
                <div style='background-color: {barColor}; width: {Math.Min(100, Math.Max(0, vol.UsedPercentage))}%; height: 100%; border-radius: 4px;'></div>
              </div>
              <div style='font-size: 12px; color: #64748b; display: flex; justify-content: space-between;'>
                <span>Kullanılan: <strong>{vol.UsedSizeGb} GB</strong> / Toplam: <strong>{vol.TotalSizeGb} GB</strong></span>
                <span>Boş Alan: <strong>{vol.FreeSizeGb} GB</strong> ({vol.DriveFormat})</span>
              </div>
            </div>");
        }

        var sbPhysical = new StringBuilder();
        foreach (var p in report.PhysicalDisks)
        {
            string healthColor = p.Status.Equals("OK", StringComparison.OrdinalIgnoreCase) ? "#10b981" : "#ef4444";
            sbPhysical.Append($@"
            <tr style='border-bottom: 1px solid #e2e8f0;'>
              <td style='padding: 8px 12px; font-weight: 500;'>{p.Model}</td>
              <td style='padding: 8px 12px; color: #64748b;'>{p.InterfaceType} / {p.MediaType}</td>
              <td style='padding: 8px 12px; font-weight: bold;'>{p.SizeGb} GB</td>
              <td style='padding: 8px 12px;'><span style='color: {healthColor}; font-weight: bold;'>{p.Status}</span></td>
            </tr>");
        }

        string alertBanner = string.Empty;
        if (!string.IsNullOrWhiteSpace(extraAlertMessage))
        {
            alertBanner = $@"
            <div style='background-color: #fee2e2; border-left: 4px solid #ef4444; color: #991b1b; padding: 12px 16px; margin-bottom: 20px; border-radius: 4px;'>
              <strong>DİKKAT:</strong> {extraAlertMessage}
            </div>";
        }

        return $@"
<!DOCTYPE html>
<html>
<head>
<meta charset='utf-8'>
<style>
  body {{ font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif; background-color: #f1f5f9; margin: 0; padding: 20px; color: #1e293b; }}
  .container {{ max-width: 650px; margin: 0 auto; background: #ffffff; border-radius: 10px; overflow: hidden; box-shadow: 0 4px 6px -1px rgba(0, 0, 0, 0.1); }}
  .header {{ background: #1e293b; color: #ffffff; padding: 24px; text-align: left; }}
  .header h1 {{ margin: 0; font-size: 20px; font-weight: 700; }}
  .badge {{ display: inline-block; padding: 6px 14px; background: {badgeBg}; color: #ffffff; font-weight: bold; border-radius: 9999px; font-size: 13px; margin-top: 10px; }}
  .content {{ padding: 24px; }}
  .section-title {{ font-size: 16px; font-weight: 600; color: #0f172a; margin: 20px 0 12px 0; border-bottom: 2px solid #e2e8f0; padding-bottom: 6px; }}
  .table {{ width: 100%; border-collapse: collapse; font-size: 13px; }}
  .table th {{ background: #f8fafc; text-align: left; padding: 8px 12px; color: #475569; }}
  .footer {{ background: #f8fafc; padding: 16px 24px; text-align: center; font-size: 12px; color: #64748b; border-top: 1px solid #e2e8f0; }}
</style>
</head>
<body>
  <div class='container'>
    <div class='header'>
      <div style='font-size: 12px; text-transform: uppercase; letter-spacing: 1px; color: #94a3b8;'>VDA HUB SERVER CONTROLLER</div>
      <h1>{report.ServerName}</h1>
      <div style='font-size: 13px; color: #cbd5e1; margin-top: 4px;'>Sunucu: {report.MachineName} • İşletim Sistemi: {report.OsVersion}</div>
      <div class='badge'>{statusText}</div>
    </div>
    <div class='content'>
      {alertBanner}

      <div class='section-title'>📁 Mantıksal Sürücüler (Bölüntüler)</div>
      {sbVolumes}

      {(report.PhysicalDisks.Count > 0 ? $@"
      <div class='section-title'>💽 Fiziksel Diskler & SMART Sağlık Durumu</div>
      <table class='table'>
        <thead>
          <tr>
            <th>Disk Modeli</th>
            <th>Arayüz</th>
            <th>Kapasite</th>
            <th>Durum</th>
          </tr>
        </thead>
        <tbody>
          {sbPhysical}
        </tbody>
      </table>" : "")}

      <div style='margin-top: 24px; padding: 12px; background: #f0fdf4; border-radius: 6px; font-size: 13px; color: #166534;'>
        <strong>Özet Durum:</strong> {report.SummaryMessage}
      </div>
    </div>
    <div class='footer'>
      Bu rapor VDA Hub Server Controller tarafından otomatik üretilmiştir.<br/>
      Rapor Saati: {DateTime.Now:yyyy-MM-dd HH:mm:ss} (Yerel Saat)
    </div>
  </div>
</body>
</html>";
    }
}
