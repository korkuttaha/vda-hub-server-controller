using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using VdaHubServerController.Background;
using VdaHubServerController.Services;
using VdaHubServerController.UI;

namespace VdaHubServerController;

internal static class Program
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);
    private const int ATTACH_PARENT_PROCESS = -1;

    private static Mutex? _singleInstanceMutex;

    [STAThread]
    static async Task<int> Main(string[] args)
    {
        // If launched with arguments from terminal, attach to parent console so output is visible
        if (args.Length > 0 && !args.Contains("--service", StringComparer.OrdinalIgnoreCase))
        {
            AttachConsole(ATTACH_PARENT_PROCESS);
        }

        // 1. Command-line: Help
        if (args.Contains("--help", StringComparer.OrdinalIgnoreCase) || args.Contains("-h", StringComparer.OrdinalIgnoreCase))
        {
            PrintHelp();
            return 0;
        }

        // 2. Command-line: Windows Service Mode
        if (args.Contains("--service", StringComparer.OrdinalIgnoreCase))
        {
            return await RunAsWindowsServiceAsync(args);
        }

        // 3. Command-line: Service Installation / Lifecycle
        if (args.Contains("--install-service", StringComparer.OrdinalIgnoreCase))
        {
            var installConfig = new ConfigService();
            if (!EnrollmentService.IsEnrolled(installConfig.Current))
            {
                Console.WriteLine("Önce EXE'yi normal açıp Hub Kurulum Anahtarı ile eşleştirin.");
                return 1;
            }
            string exePath = Environment.ProcessPath ?? AppDomain.CurrentDomain.BaseDirectory;
            var (success, msg) = WindowsServiceManager.InstallService(exePath);
            Console.WriteLine(msg);
            return success ? 0 : 1;
        }

        if (args.Contains("--uninstall-service", StringComparer.OrdinalIgnoreCase))
        {
            var (success, msg) = WindowsServiceManager.UninstallService();
            Console.WriteLine(msg);
            return success ? 0 : 1;
        }

        if (args.Contains("--start-service", StringComparer.OrdinalIgnoreCase))
        {
            var (success, msg) = WindowsServiceManager.StartService();
            Console.WriteLine(msg);
            return success ? 0 : 1;
        }

        if (args.Contains("--stop-service", StringComparer.OrdinalIgnoreCase))
        {
            var (success, msg) = WindowsServiceManager.StopService();
            Console.WriteLine(msg);
            return success ? 0 : 1;
        }

        // 4. Command-line: Diagnostics & Testing
        var configService = new ConfigService();
        var diskMonitor = new DiskMonitorService();
        var brevoService = new BrevoEmailService();
        var hubClient = new HubClientService();
        var backupService = new BackupService(configService);
        var engine = new ControllerEngine(configService, diskMonitor, brevoService, hubClient, backupService);

        if (args.Contains("--test-mail", StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine("\n[VDA Hub] Brevo test e-postası gönderiliyor...");
            var (success, msg) = await brevoService.SendTestEmailAsync(configService.Current.Brevo, configService.Current.ServerName);
            Console.WriteLine(success ? $"✓ {msg}" : $"✗ {msg}");
            return success ? 0 : 1;
        }

        if (args.Contains("--test-hub", StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine("\n[VDA Hub] VDA Hub test raporu iletiliyor...");
            var report = diskMonitor.CollectReport(configService.Current);
            var (success, msg, code) = await hubClient.SendReportAsync(configService.Current.Hub, report);
            Console.WriteLine(success ? $"✓ [HTTP {code}] {msg}" : $"✗ [HTTP {code}] {msg}");
            return success ? 0 : 1;
        }

        if (args.Contains("--backup-now", StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine("\n[VDA Hub] Dropbox yedekleme zorla başlatılıyor...");
            var result = await backupService.RunIfDueAsync(force: true);
            Console.WriteLine(result.Success ? $"✓ {result.Message}" : $"✗ {result.Message}");
            return result.Success ? 0 : 1;
        }

        if (args.Contains("--run-once", StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine("\n[VDA Hub] Tek seferlik tarama ve senkronizasyon başlatılıyor...");
            await engine.ExecuteCheckCycleAsync(forceHub: configService.Current.Hub.Enabled);
            Console.WriteLine("[VDA Hub] Tamamlandı.\n");
            return 0;
        }

        // 5. GUI & System Tray Mode
        const string mutexName = "Global\\VdaHubServerController_Mutex";
        _singleInstanceMutex = new Mutex(true, mutexName, out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show(
                "VDA Hub Server Controller zaten arka planda çalışıyor.\nLütfen ekranın sağ altındaki Sistem Tepsisi (Tray) simgesini kontrol edin.",
                "Uygulama Çalışıyor", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        ApplicationConfiguration.Initialize();

        if (!EnrollmentService.IsEnrolled(configService.Current))
        {
            using var enrollmentForm = new EnrollmentForm(new EnrollmentService(configService));
            if (enrollmentForm.ShowDialog() != DialogResult.OK)
            {
                _singleInstanceMutex.ReleaseMutex();
                return 1;
            }
        }

        // Start background worker loop for the GUI session
        using var cts = new CancellationTokenSource();
        _ = Task.Run(() => engine.StartLoopAsync(cts.Token));

        var mainForm = new MainForm(configService, engine, brevoService, hubClient, backupService);

        // Run UI message loop
        Application.Run(mainForm);

        cts.Cancel();
        _singleInstanceMutex.ReleaseMutex();
        return 0;
    }

    private static void PrintHelp()
    {
        Console.WriteLine(@"
============================================================
              VDA HUB SERVER CONTROLLER (.NET 9)
============================================================
Kullanım:
  VdaHubServerController.exe [seçenek]

Seçenekler:
  (parametre yok)     : Grafik Arayüzü (Dashboard & System Tray) modunda başlatır.
  --service           : Headless Windows Service modunda çalıştırır.
  --install-service   : Uygulamayı Windows Servisi olarak kurar (Otomatik başlatma).
  --uninstall-service : Kurulu Windows Servisini sistemden kaldırır.
  --start-service     : Windows Servisini başlatır.
  --stop-service      : Windows Servisini durdurur.
  --run-once          : Diskleri tek sefer tarar, raporu üretir ve çıkar.
  --test-mail         : Brevo API ile yapılandırılan adrese test e-postası yollar.
  --test-hub          : VDA Hub API uç noktasına test raporu gönderir.
  --backup-now        : Dropbox klasör yedeklemesini zaman beklemeden çalıştırır.
  --help, -h          : Bu yardım menüsünü görüntüler.

İlk kurulum:
  Hub > Sunucular ekranında sunucu kaydını açın ve tek kullanımlık Kurulum Anahtarını alın.
  EXE ilk açılışta bu anahtarı sorar; ServerId ve kalıcı API anahtarı otomatik kaydedilir.
");
    }

    private static async Task<int> RunAsWindowsServiceAsync(string[] args)
    {
        try
        {
            var builder = Host.CreateDefaultBuilder(args)
                .UseWindowsService(options =>
                {
                    options.ServiceName = WindowsServiceManager.ServiceName;
                })
                .ConfigureServices((hostContext, services) =>
                {
                    services.AddSingleton<ConfigService>();
                    services.AddSingleton<DiskMonitorService>();
                    services.AddSingleton<BrevoEmailService>();
                    services.AddSingleton<HubClientService>();
                    services.AddSingleton<BackupService>();
                    services.AddSingleton<ControllerEngine>();
                    services.AddHostedService<WorkerService>();
                });

            var host = builder.Build();
            await host.RunAsync();
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Windows Servis hatası: {ex.Message}");
            return 1;
        }
    }
}