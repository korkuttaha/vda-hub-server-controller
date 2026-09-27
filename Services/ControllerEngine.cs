using VdaHubServerController.Models;

namespace VdaHubServerController.Services;

public class ControllerEngine
{
    private readonly ConfigService _configService;
    private readonly DiskMonitorService _diskMonitor;
    private readonly BrevoEmailService _brevoService;
    private readonly HubClientService _hubClient;
    private readonly BackupService _backupService;

    public event Action<string>? OnLog;
    public event Action<ServerStatusReport>? OnReportUpdated;

    public ServerStatusReport? LastReport { get; private set; }

    public ControllerEngine(
        ConfigService configService,
        DiskMonitorService diskMonitor,
        BrevoEmailService brevoService,
        HubClientService hubClient,
        BackupService backupService)
    {
        _configService = configService;
        _diskMonitor = diskMonitor;
        _brevoService = brevoService;
        _hubClient = hubClient;
        _backupService = backupService;
    }

    private void Log(string message)
    {
        string formatted = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
        Console.WriteLine(formatted);
        OnLog?.Invoke(formatted);
    }

    public async Task<ServerStatusReport> ExecuteCheckCycleAsync(
        bool forceEmail = false,
        bool forceHub = false,
        CancellationToken ct = default)
    {
        var config = _configService.Current;
        Log("Disk durumu taranıyor...");

        var report = _diskMonitor.CollectReport(config);
        LastReport = report;
        OnReportUpdated?.Invoke(report);

        Log($"Tarama tamamlandı: {report.Volumes.Count} bölüm, {report.PhysicalDisks.Count} fiziksel disk bulundu. Genel durum: {report.OverallStatus}");

        // 1. Hub Raporu
        if (config.Hub.Enabled || forceHub)
        {
            Log($"VDA Hub'a rapor gönderiliyor ({config.Hub.HubApiUrl})...");
            var (hubSuccess, hubMsg, _) = await _hubClient.SendReportAsync(config.Hub, report, ct);
            Log(hubSuccess ? $"✓ VDA Hub: {hubMsg}" : $"✗ VDA Hub Hatası: {hubMsg}");
        }

        // 2. Brevo E-Posta İşlemleri
        if (config.Brevo.Enabled || forceEmail)
        {
            await HandleBrevoNotificationsAsync(config, report, forceEmail, ct);
        }

        return report;
    }

    private async Task HandleBrevoNotificationsAsync(
        AppConfig config,
        ServerStatusReport report,
        bool forceEmail,
        CancellationToken ct)
    {
        var brevo = config.Brevo;

        // A. Manuel zorunlu e-posta (Test / Manuel Tetikleme)
        if (forceEmail)
        {
            Log("Brevo: Manuel istek üzerine günlük e-posta raporu gönderiliyor...");
            var (success, msg) = await _brevoService.SendDailyReportAsync(brevo, report);
            Log(success ? $"✓ Brevo: {msg}" : $"✗ Brevo Hatası: {msg}");
            return;
        }

        // B. Günlük Planlanmış Rapor (Örn: 09:00)
        string todayStr = DateTime.Now.ToString("yyyy-MM-dd");
        if (TimeSpan.TryParse(brevo.DailyReportTime, out TimeSpan targetTime))
        {
            var nowTime = DateTime.Now.TimeOfDay;
            // Eğer hedef saat geçildiyse ve bugün henüz gönderilmediyse
            if (nowTime >= targetTime && brevo.LastDailyReportDate != todayStr)
            {
                Log($"Brevo: Günlük rapor saati ({brevo.DailyReportTime}) geldi. Rapor gönderiliyor...");
                var (success, msg) = await _brevoService.SendDailyReportAsync(brevo, report);
                if (success)
                {
                    Log($"✓ Brevo: Günlük rapor başarıyla gönderildi: {msg}");
                    brevo.LastDailyReportDate = todayStr;
                    _configService.Save();
                }
                else
                {
                    Log($"✗ Brevo: Günlük rapor gönderilemedi: {msg}");
                }
            }
        }

        // C. Kritik Durum Anlık Alarmı
        if (brevo.SendCriticalAlertImmediately && report.OverallStatus == "CRITICAL")
        {
            // Spam engellemek için son 4 saatte kritik alarm atılmadıysa gönder
            bool shouldAlert = !brevo.LastCriticalAlertSentAt.HasValue ||
                               (DateTime.UtcNow - brevo.LastCriticalAlertSentAt.Value) > TimeSpan.FromHours(4);

            if (shouldAlert)
            {
                Log("Brevo: KRİTİK SEVİYE TESPİT EDİLDİ! Anlık alarm e-postası gönderiliyor...");
                var (alertSuccess, alertMsg) = await _brevoService.SendCriticalAlertAsync(brevo, report, report.SummaryMessage);
                if (alertSuccess)
                {
                    Log($"✓ Brevo Kritik Alarm yollandı: {alertMsg}");
                    brevo.LastCriticalAlertSentAt = DateTime.UtcNow;
                    _configService.Save();
                }
                else
                {
                    Log($"✗ Brevo Kritik Alarm hatası: {alertMsg}");
                }
            }
        }
    }

    public async Task StartLoopAsync(CancellationToken ct)
    {
        Log("Arka plan izleme döngüsü başlatıldı.");
        var backupLoop = RunBackupCommandLoopAsync(ct);

        // İlk döngüyü hemen çalıştır
        try
        {
            await ExecuteCheckCycleAsync(ct: ct);
        }
        catch (Exception ex)
        {
            Log($"İlk kontrol sırasında hata: {ex.Message}");
        }

        while (!ct.IsCancellationRequested)
        {
            int intervalMinutes = Math.Max(1, _configService.Current.CheckIntervalMinutes);
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(intervalMinutes), ct);
                if (ct.IsCancellationRequested) break;

                await ExecuteCheckCycleAsync(ct: ct);
            }
            catch (TaskCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log($"Kontrol döngüsü hatası: {ex.Message}");
            }
        }

        try { await backupLoop; } catch (TaskCanceledException) { }
        Log("Arka plan izleme döngüsü sonlandırıldı.");
    }

    private async Task RunBackupCommandLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_configService.Current.Hub.Enabled)
                {
                    var backup = await _backupService.RunPendingAsync(ct);
                    if (backup.Ran)
                        Log(backup.Success ? $"✓ Dropbox Snapshot: {backup.Message}" : $"✗ Dropbox Snapshot: {backup.Message}");
                }
                await Task.Delay(TimeSpan.FromSeconds(15), ct);
            }
            catch (TaskCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log($"Yedekleme komutu kontrol hatası: {ex.Message}");
                await Task.Delay(TimeSpan.FromSeconds(15), ct);
            }
        }
    }
}
