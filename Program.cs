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
        if (args.Length > 0 &&
            !args.Contains("--service", StringComparer.OrdinalIgnoreCase) &&
            !args.Contains("--pc-service", StringComparer.OrdinalIgnoreCase))
        {
            AttachConsole(ATTACH_PARENT_PROCESS);
        }

        // 1. Command-line: Help
        if (args.Contains("--help", StringComparer.OrdinalIgnoreCase) || args.Contains("-h", StringComparer.OrdinalIgnoreCase))
        {
            PrintHelp();
            return 0;
        }

        // 2. Personal PC Agent mode is intentionally separate from the server controller.
        if (args.Contains("--pc-service", StringComparer.OrdinalIgnoreCase))
        {
            return await RunAsPcServiceAsync(args);
        }

        var executableName = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? string.Empty);
        var pcAgentUi = args.Contains("--pc-agent", StringComparer.OrdinalIgnoreCase) ||
            args.Contains("--reset", StringComparer.OrdinalIgnoreCase) ||
            args.Contains("--re-enroll", StringComparer.OrdinalIgnoreCase) ||
            executableName.StartsWith("VDAKorPcAgent", StringComparison.OrdinalIgnoreCase);
        if (pcAgentUi)
        {
            return RunPcAgentUi(args);
        }

        // 3. Server Controller Windows Service Mode
        if (args.Contains("--service", StringComparer.OrdinalIgnoreCase))
        {
            return await RunAsWindowsServiceAsync(args);
        }

        // 4. Server Controller service installation / lifecycle
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

        // 5. Command-line: Diagnostics & Testing
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

        // 6. Server Controller one-time enrollment / on-demand settings UI.
        const string mutexName = "Global\\VdaHubServerController_Mutex";
        _singleInstanceMutex = new Mutex(true, mutexName, out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show(
                "VDA Hub Server Controller ayar penceresi zaten açık. Arka plan ajanı Windows servisi olarak çalışır.",
                "Uygulama Çalışıyor", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        ApplicationConfiguration.Initialize();

        var newlyEnrolled = false;
        if (!EnrollmentService.IsEnrolled(configService.Current))
        {
            using var enrollmentForm = new EnrollmentForm(new EnrollmentService(configService));
            if (enrollmentForm.ShowDialog() != DialogResult.OK)
            {
                _singleInstanceMutex.ReleaseMutex();
                return 1;
            }
            newlyEnrolled = true;
        }

        var executable = Environment.ProcessPath ?? Application.ExecutablePath;
        var (installed, installMessage) = WindowsServiceManager.InstallService(executable);
        if (!installed)
        {
            MessageBox.Show(installMessage, "Servis kurulamadı", MessageBoxButtons.OK, MessageBoxIcon.Error);
            _singleInstanceMutex.ReleaseMutex();
            return 1;
        }
        var (started, startMessage) = WindowsServiceManager.StartService();
        if (!started)
        {
            MessageBox.Show(startMessage, "Servis başlatılamadı", MessageBoxButtons.OK, MessageBoxIcon.Error);
            _singleInstanceMutex.ReleaseMutex();
            return 1;
        }

        if (newlyEnrolled)
        {
            MessageBox.Show(
                "Eşleştirme tamamlandı. Eski ajan servisi kaldırıldı ve yeni headless servis başlatıldı.\n\nAjan taskbar veya sistem tepsisinde ikon göstermeden arka planda çalışacak.",
                "VDA Hub ajanı hazır",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            _singleInstanceMutex.ReleaseMutex();
            return 0;
        }

        var mainForm = new MainForm(configService, engine, brevoService, hubClient, backupService);
        Application.Run(mainForm);

        _singleInstanceMutex.ReleaseMutex();
        return 0;
    }

    private static int RunPcAgentUi(string[] args)
    {
        const string mutexName = "Global\\VDAKorPcAgent_UI_Mutex";
        using var mutex = new Mutex(true, mutexName, out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show(
                "VDAKor PC Agent kurulum penceresi zaten açık.",
                "VDAKor PC Agent",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return 0;
        }

        ApplicationConfiguration.Initialize();
        var config = new PcConfigService();

        var resetRequested = args.Contains("--reset", StringComparer.OrdinalIgnoreCase) ||
            args.Contains("--re-enroll", StringComparer.OrdinalIgnoreCase);

        if (resetRequested)
        {
            PcWindowsServiceManager.ResetService();
            config.Reset();
        }

        var isEnrolled = PcEnrollmentService.IsEnrolled(config.Current);
        if (!isEnrolled)
        {
            using var form = new PcEnrollmentForm(new PcEnrollmentService(config));
            if (form.ShowDialog() != DialogResult.OK)
                return 1;
        }
        else
        {
            using var statusForm = new PcStatusForm(config);
            var statusResult = statusForm.ShowDialog();
            if (statusResult == DialogResult.Retry)
            {
                using var form = new PcEnrollmentForm(new PcEnrollmentService(config));
                if (form.ShowDialog() != DialogResult.OK)
                    return 1;
            }
            else
            {
                return 0;
            }
        }

        var executable = Environment.ProcessPath ?? Application.ExecutablePath;
        var (installed, installMessage) = PcWindowsServiceManager.InstallService(executable, forceRestart: true);
        if (!installed)
        {
            MessageBox.Show(installMessage, "PC Agent servisi kurulamadı", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }

        var (started, startMessage) = PcWindowsServiceManager.StartService();
        if (!started && !PcWindowsServiceManager.GetServiceStatus().StartsWith("Çalışıyor", StringComparison.Ordinal))
        {
            MessageBox.Show(startMessage, "PC Agent başlatılamadı", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }

        MessageBox.Show(
            $"{config.Current.ComputerName} VDAKor Hub'a bağlı.\n\nPC Agent arka planda Windows servisi olarak çalışıyor.",
            "VDAKor PC Agent hazır",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
        return 0;
    }

    private static async Task<int> RunAsPcServiceAsync(string[] args)
    {
        try
        {
            var builder = Host.CreateDefaultBuilder(args)
                .UseWindowsService(options =>
                {
                    options.ServiceName = PcWindowsServiceManager.ServiceName;
                })
                .ConfigureServices((hostContext, services) =>
                {
                    services.AddSingleton<PcConfigService>();
                    services.AddSingleton<PcPowerCommandService>();
                    services.AddSingleton<PcAgentEngine>();
                    services.AddHostedService<PcWorkerService>();
                });

            var host = builder.Build();
            await host.RunAsync();
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"VDAKor PC Agent servis hatası: {ex.Message}");
            return 1;
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine(@"
============================================================
              VDA HUB SERVER CONTROLLER (.NET 9)
============================================================
Kullanım:
  VdaHubServerController.exe [seçenek]

Kişisel PC modu:
  VDAKorPcAgent.exe          : Hub > Bilgisayarlar koduyla kişisel PC eşleştirmesi veya durum ekranı.
  --pc-agent                : Aynı kişisel PC kurulum modunu açıkça başlatır.
  --reset, --re-enroll      : Önceki servisi ve yapılandırmayı sıfırlayarak temiz eşleştirme ekranını açar.
  --pc-service              : VDAKor PC Agent Windows servisi (arka plan).

Seçenekler:
  (parametre yok)     : İlk kurulumda eşleştirir; sonrasında geçici ayar ekranını açar.
  --service           : Headless Windows Service modunda çalıştırır.
  --install-service   : Uygulamayı Windows Servisi olarak kurar (Otomatik başlatma).
  --uninstall-service : Kurulu Windows Servisini sistemden kaldırır.
  --start-service     : Windows Servisini başlatır.
  --stop-service      : Windows Servisini durdurur.
  --run-once          : Diskleri tek sefer tarar, raporu üretir ve çıkar.
  --test-mail         : Brevo API ile yapılandırılan adrese test e-postası yollar.
  --test-hub          : VDA Hub API uç noktasına test raporu gönderir.
  --backup-now        : Hub'da bekleyen manuel Dropbox snapshot komutunu çalıştırır.
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
