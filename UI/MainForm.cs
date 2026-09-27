using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using VdaHubServerController.Models;
using VdaHubServerController.Services;

namespace VdaHubServerController.UI;

public class MainForm : Form
{
    private readonly ConfigService _configService;
    private readonly ControllerEngine _engine;
    private readonly BrevoEmailService _brevoService;
    private readonly HubClientService _hubClient;
    private readonly BackupService _backupService;

    // Tray & Form controls
    private NotifyIcon _trayIcon = null!;
    private ContextMenuStrip _trayMenu = null!;
    private TabControl _tabControl = null!;
    
    // Top banner
    private Label _lblServerTitle = null!;
    private Label _lblServerMeta = null!;
    private Label _lblStatusBadge = null!;
    private Button _btnScanNow = null!;
    private Button _btnMinimizeToTray = null!;

    // Dashboard controls
    private ListView _lvVolumes = null!;
    private ListView _lvPhysicalDisks = null!;
    private Label _lblSummary = null!;

    // Settings controls
    private TextBox _txtServerName = null!;
    private NumericUpDown _numInterval = null!;
    private CheckBox _chkHubEnabled = null!;
    private TextBox _txtHubUrl = null!;
    private TextBox _txtHubApiKey = null!;
    private CheckBox _chkBrevoEnabled = null!;
    private TextBox _txtBrevoApiKey = null!;
    private TextBox _txtBrevoSenderEmail = null!;
    private TextBox _txtBrevoSenderName = null!;
    private TextBox _txtBrevoRecipients = null!;
    private TextBox _txtBrevoDailyTime = null!;
    private CheckBox _chkBrevoCriticalAlert = null!;
    private NumericUpDown _numCriticalThreshold = null!;
    private Button _btnSaveSettings = null!;
    private Button _btnTestBrevo = null!;
    private Button _btnTestHub = null!;

    // Backup controls
    private CheckBox _chkBackupEnabled = null!;
    private ListBox _lstBackupFolders = null!;
    private NumericUpDown _numBackupHour = null!;
    private Label _lblBackupStatus = null!;
    private Button _btnBackupAddFolder = null!;
    private Button _btnBackupRemoveFolder = null!;
    private Button _btnBackupSave = null!;
    private Button _btnBackupRunNow = null!;

    // Service controls
    private Label _lblServiceStatus = null!;
    private Button _btnInstallService = null!;
    private Button _btnUninstallService = null!;
    private Button _btnStartService = null!;
    private Button _btnStopService = null!;

    // Logs controls
    private TextBox _txtLogs = null!;

    public MainForm(
        ConfigService configService,
        ControllerEngine engine,
        BrevoEmailService brevoService,
        HubClientService hubClient,
        BackupService backupService)
    {
        _configService = configService;
        _engine = engine;
        _brevoService = brevoService;
        _hubClient = hubClient;
        _backupService = backupService;

        InitializeComponent();
        SetupEvents();
        LoadSettingsIntoUI();
        RefreshServiceStatus();
        this.Shown += async (_, _) => await LoadBackupPlanAsync();
    }

    private void InitializeComponent()
    {
        this.Text = "VDA Hub Server Controller";
        this.Size = new Size(880, 680);
        this.MinimumSize = new Size(780, 580);
        this.StartPosition = FormStartPosition.CenterScreen;
        this.Font = new Font("Segoe UI", 9.25f, FontStyle.Regular);
        this.BackColor = Color.FromArgb(244, 246, 249);
        this.Icon = SystemIcons.Shield;

        // Tray Icon setup
        _trayMenu = new ContextMenuStrip();
        _trayMenu.Items.Add("Göster (Dashboard)", null, (s, e) => ShowAndRestore());
        _trayMenu.Items.Add("-");
        _trayMenu.Items.Add("Şimdi Tara & Gönder", null, async (s, e) => await TriggerManualScan());
        _trayMenu.Items.Add("Brevo Test E-postası", null, async (s, e) => await TriggerTestEmail());
        _trayMenu.Items.Add("-");
        _trayMenu.Items.Add("Çıkış", null, (s, e) => ExitApplication());

        _trayIcon = new NotifyIcon
        {
            Icon = SystemIcons.Shield,
            Text = "VDA Hub Server Controller",
            Visible = true,
            ContextMenuStrip = _trayMenu
        };
        _trayIcon.DoubleClick += (s, e) => ShowAndRestore();

        // 1. Top Banner
        var pnlHeader = new Panel
        {
            Dock = DockStyle.Top,
            Height = 85,
            BackColor = Color.FromArgb(30, 41, 59),
            Padding = new Padding(20, 12, 20, 12)
        };

        _lblServerTitle = new Label
        {
            Text = "VDA HUB SERVER CONTROLLER",
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 13.5f, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(20, 14)
        };

        _lblServerMeta = new Label
        {
            Text = $"Sunucu: {Environment.MachineName} • İşletim Sistemi: {Environment.OSVersion}",
            ForeColor = Color.FromArgb(148, 163, 184),
            Font = new Font("Segoe UI", 9.0f, FontStyle.Regular),
            AutoSize = true,
            Location = new Point(20, 44)
        };

        _lblStatusBadge = new Label
        {
            Text = "Tarama Bekleniyor...",
            ForeColor = Color.White,
            BackColor = Color.FromArgb(71, 85, 105),
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            AutoSize = false,
            Size = new Size(200, 32),
            TextAlign = ContentAlignment.MiddleCenter,
            Location = new Point(410, 24),
            Anchor = AnchorStyles.Top | AnchorStyles.Right
        };

        _btnScanNow = new Button
        {
            Text = "🔄 Şimdi Tara",
            Size = new Size(110, 34),
            Location = new Point(625, 23),
            BackColor = Color.FromArgb(37, 99, 235),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand,
            Anchor = AnchorStyles.Top | AnchorStyles.Right
        };
        _btnScanNow.FlatAppearance.BorderSize = 0;
        _btnScanNow.Click += async (s, e) => await TriggerManualScan();

        _btnMinimizeToTray = new Button
        {
            Text = "Arka Plana Al",
            Size = new Size(110, 34),
            Location = new Point(745, 23),
            BackColor = Color.FromArgb(51, 65, 85),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand,
            Anchor = AnchorStyles.Top | AnchorStyles.Right
        };
        _btnMinimizeToTray.FlatAppearance.BorderSize = 0;
        _btnMinimizeToTray.Click += (s, e) => MinimizeToTray();

        pnlHeader.Controls.AddRange(new Control[] { _lblServerTitle, _lblServerMeta, _lblStatusBadge, _btnScanNow, _btnMinimizeToTray });

        // 2. Tab Control
        _tabControl = new TabControl
        {
            Dock = DockStyle.Fill,
            Padding = new Point(14, 8)
        };

        // Tab 1: Dashboard
        var tabDashboard = CreateDashboardTab();
        // Tab 2: Settings
        var tabSettings = CreateSettingsTab();
        // Tab 3: Dropbox Backup
        var tabBackup = CreateBackupTab();
        // Tab 4: Windows Service
        var tabService = CreateServiceTab();
        // Tab 5: Logs
        var tabLogs = CreateLogsTab();

        _tabControl.TabPages.Add(tabDashboard);
        _tabControl.TabPages.Add(tabSettings);
        _tabControl.TabPages.Add(tabBackup);
        _tabControl.TabPages.Add(tabService);
        _tabControl.TabPages.Add(tabLogs);

        this.Controls.Add(_tabControl);
        this.Controls.Add(pnlHeader);
    }

    private TabPage CreateDashboardTab()
    {
        var tab = new TabPage("📊 Disk Durumu (Dashboard)");
        tab.Padding = new Padding(15);
        tab.BackColor = Color.White;

        var lblVolTitle = new Label
        {
            Text = "📁 Mantıksal Sürücüler (Bölümler)",
            Font = new Font("Segoe UI", 10.5f, FontStyle.Bold),
            ForeColor = Color.FromArgb(15, 23, 42),
            Dock = DockStyle.Top,
            Height = 28
        };

        _lvVolumes = new ListView
        {
            Dock = DockStyle.Top,
            Height = 160,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true
        };
        _lvVolumes.Columns.Add("Sürücü", 80);
        _lvVolumes.Columns.Add("Etiket", 110);
        _lvVolumes.Columns.Add("Doluluk (%)", 110);
        _lvVolumes.Columns.Add("Kullanılan (GB)", 110);
        _lvVolumes.Columns.Add("Boş Alan (GB)", 110);
        _lvVolumes.Columns.Add("Toplam (GB)", 110);
        _lvVolumes.Columns.Add("Dosya Sistemi", 90);
        _lvVolumes.Columns.Add("Durum", 90);

        var lblPhysTitle = new Label
        {
            Text = "💽 Fiziksel Diskler & SMART Sağlık Bilgisi",
            Font = new Font("Segoe UI", 10.5f, FontStyle.Bold),
            ForeColor = Color.FromArgb(15, 23, 42),
            Dock = DockStyle.Top,
            Height = 32
        };

        _lvPhysicalDisks = new ListView
        {
            Dock = DockStyle.Top,
            Height = 150,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true
        };
        _lvPhysicalDisks.Columns.Add("Aygıt / ID", 150);
        _lvPhysicalDisks.Columns.Add("Model", 260);
        _lvPhysicalDisks.Columns.Add("Arayüz / Tür", 130);
        _lvPhysicalDisks.Columns.Add("Boyut (GB)", 110);
        _lvPhysicalDisks.Columns.Add("SMART Durumu", 120);

        var pnlSummary = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(10),
            BackColor = Color.FromArgb(248, 250, 252)
        };

        _lblSummary = new Label
        {
            Text = "Sistem henüz taranmadı. 'Şimdi Tara' butonuna basarak kontrol başlatabilirsiniz.",
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Regular),
            ForeColor = Color.FromArgb(51, 65, 85)
        };
        pnlSummary.Controls.Add(_lblSummary);

        // Add controls in reverse dock order for correct top-to-bottom layout
        tab.Controls.Add(pnlSummary);
        tab.Controls.Add(_lvPhysicalDisks);
        tab.Controls.Add(lblPhysTitle);
        tab.Controls.Add(_lvVolumes);
        tab.Controls.Add(lblVolTitle);

        return tab;
    }

    private TabPage CreateSettingsTab()
    {
        var tab = new TabPage("⚙️ Ayarlar & Bildirimler");
        tab.AutoScroll = true;
        tab.BackColor = Color.White;
        tab.Padding = new Padding(20);

        int y = 15;

        // Group 1: General Server
        var grpServer = new GroupBox
        {
            Text = "🖥️ Sunucu Ayarları",
            Location = new Point(15, y),
            Size = new Size(810, 95),
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold)
        };

        var lblName = new Label { Text = "Sunucu Görünen Adı:", Location = new Point(20, 30), AutoSize = true, Font = new Font("Segoe UI", 9.0f) };
        _txtServerName = new TextBox { Location = new Point(160, 27), Size = new Size(260, 24), Font = new Font("Segoe UI", 9.0f) };

        var lblInterval = new Label { Text = "Tarama Aralığı (Dakika):", Location = new Point(450, 30), AutoSize = true, Font = new Font("Segoe UI", 9.0f) };
        _numInterval = new NumericUpDown { Location = new Point(610, 27), Size = new Size(80, 24), Minimum = 1, Maximum = 1440, Value = 5, Font = new Font("Segoe UI", 9.0f) };

        grpServer.Controls.AddRange(new Control[] { lblName, _txtServerName, lblInterval, _numInterval });
        tab.Controls.Add(grpServer);

        y += 110;

        // Group 2: VDA Hub Settings
        var grpHub = new GroupBox
        {
            Text = "🌐 VDA Hub Entegrasyonu",
            Location = new Point(15, y),
            Size = new Size(810, 140),
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold)
        };

        _chkHubEnabled = new CheckBox { Text = "VDA Hub'a Durum Raporlarını Gönder", Location = new Point(20, 28), AutoSize = true, Font = new Font("Segoe UI", 9.0f, FontStyle.Bold) };

        var lblHubUrl = new Label { Text = "Hub API URL:", Location = new Point(20, 60), AutoSize = true, Font = new Font("Segoe UI", 9.0f) };
        _txtHubUrl = new TextBox { Location = new Point(160, 57), Size = new Size(450, 24), Font = new Font("Segoe UI", 9.0f) };

        var lblHubKey = new Label { Text = "API Key / Token:", Location = new Point(20, 95), AutoSize = true, Font = new Font("Segoe UI", 9.0f) };
        _txtHubApiKey = new TextBox { Location = new Point(160, 92), Size = new Size(450, 24), Font = new Font("Segoe UI", 9.0f), UseSystemPasswordChar = true };

        _btnTestHub = new Button
        {
            Text = "Hub Test Gönder",
            Location = new Point(630, 70),
            Size = new Size(150, 35),
            BackColor = Color.FromArgb(79, 70, 229),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9.0f, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        _btnTestHub.FlatAppearance.BorderSize = 0;
        _btnTestHub.Click += async (s, e) => await TriggerTestHub();

        grpHub.Controls.AddRange(new Control[] { _chkHubEnabled, lblHubUrl, _txtHubUrl, lblHubKey, _txtHubApiKey, _btnTestHub });
        tab.Controls.Add(grpHub);

        y += 155;

        // Group 3: Brevo Email Settings
        var grpBrevo = new GroupBox
        {
            Text = "✉️ Brevo E-Posta Bildirimleri (Günlük Rapor & Alarmlar)",
            Location = new Point(15, y),
            Size = new Size(810, 230),
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold)
        };

        _chkBrevoEnabled = new CheckBox { Text = "Brevo E-Posta Bildirimlerini Etkinleştir (Aktif/Pasif)", Location = new Point(20, 28), AutoSize = true, Font = new Font("Segoe UI", 9.0f, FontStyle.Bold) };

        var lblBrevoKey = new Label { Text = "Brevo API Key:", Location = new Point(20, 60), AutoSize = true, Font = new Font("Segoe UI", 9.0f) };
        _txtBrevoApiKey = new TextBox { Location = new Point(160, 57), Size = new Size(450, 24), Font = new Font("Segoe UI", 9.0f), UseSystemPasswordChar = true };

        var lblSender = new Label { Text = "Gönderen E-Posta / Ad:", Location = new Point(20, 95), AutoSize = true, Font = new Font("Segoe UI", 9.0f) };
        _txtBrevoSenderEmail = new TextBox { Location = new Point(160, 92), Size = new Size(220, 24), Font = new Font("Segoe UI", 9.0f) };
        _txtBrevoSenderName = new TextBox { Location = new Point(390, 92), Size = new Size(220, 24), Font = new Font("Segoe UI", 9.0f) };

        var lblRecipients = new Label { Text = "Alıcı E-Posta(lar):", Location = new Point(20, 130), AutoSize = true, Font = new Font("Segoe UI", 9.0f) };
        _txtBrevoRecipients = new TextBox { Location = new Point(160, 127), Size = new Size(450, 24), Font = new Font("Segoe UI", 9.0f) };

        var lblDailyTime = new Label { Text = "Günlük Rapor Saati:", Location = new Point(20, 165), AutoSize = true, Font = new Font("Segoe UI", 9.0f) };
        _txtBrevoDailyTime = new TextBox { Location = new Point(160, 162), Size = new Size(80, 24), Text = "09:00", Font = new Font("Segoe UI", 9.0f) };

        _chkBrevoCriticalAlert = new CheckBox { Text = "Kritik Eşik Aşılırsa Anında Alarm Maili Gönder", Location = new Point(270, 165), AutoSize = true, Font = new Font("Segoe UI", 9.0f) };
        _numCriticalThreshold = new NumericUpDown { Location = new Point(570, 163), Size = new Size(50, 24), Minimum = 50, Maximum = 99, Value = 90, Font = new Font("Segoe UI", 9.0f) };

        _btnTestBrevo = new Button
        {
            Text = "Brevo Test Maili Gönder",
            Location = new Point(630, 90),
            Size = new Size(160, 38),
            BackColor = Color.FromArgb(16, 185, 129),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9.0f, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        _btnTestBrevo.FlatAppearance.BorderSize = 0;
        _btnTestBrevo.Click += async (s, e) => await TriggerTestEmail();

        grpBrevo.Controls.AddRange(new Control[] {
            _chkBrevoEnabled, lblBrevoKey, _txtBrevoApiKey,
            lblSender, _txtBrevoSenderEmail, _txtBrevoSenderName,
            lblRecipients, _txtBrevoRecipients,
            lblDailyTime, _txtBrevoDailyTime,
            _chkBrevoCriticalAlert, _numCriticalThreshold,
            _btnTestBrevo
        });
        tab.Controls.Add(grpBrevo);

        y += 245;

        _btnSaveSettings = new Button
        {
            Text = "💾 Ayarları Kaydet",
            Location = new Point(15, y),
            Size = new Size(200, 42),
            BackColor = Color.FromArgb(37, 99, 235),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 10.0f, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        _btnSaveSettings.FlatAppearance.BorderSize = 0;
        _btnSaveSettings.Click += (s, e) => SaveSettingsFromUI();

        tab.Controls.Add(_btnSaveSettings);

        return tab;
    }

    private TabPage CreateBackupTab()
    {
        var tab = new TabPage("☁️ Yedekleme");
        tab.Padding = new Padding(20);
        tab.BackColor = Color.White;

        var title = new Label
        {
            Text = "Dropbox Klasör Yedekleme",
            Font = new Font("Segoe UI", 12.0f, FontStyle.Bold),
            ForeColor = Color.FromArgb(15, 23, 42),
            Location = new Point(20, 18),
            AutoSize = true
        };
        var description = new Label
        {
            Text = "Bu sunucuda yedeklenecek klasörleri Windows klasör seçicisiyle belirleyin. Dosyalar Hub üzerinden geçmeden doğrudan Dropbox'a gider. Toplu şüpheli değişiklikte aktarım otomatik durur.",
            Location = new Point(20, 50),
            Size = new Size(790, 45),
            ForeColor = Color.FromArgb(71, 85, 105)
        };

        _chkBackupEnabled = new CheckBox
        {
            Text = "Bu sunucunun Dropbox klasör yedeklemesini aç",
            Location = new Point(20, 105),
            AutoSize = true,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold)
        };

        var hourLabel = new Label { Text = "Günlük çalışma saati:", Location = new Point(480, 107), AutoSize = true };
        _numBackupHour = new NumericUpDown
        {
            Location = new Point(620, 104),
            Size = new Size(65, 25),
            Minimum = 0,
            Maximum = 23,
            Value = 2
        };

        _lstBackupFolders = new ListBox
        {
            Location = new Point(20, 145),
            Size = new Size(660, 235),
            HorizontalScrollbar = true,
            Font = new Font("Consolas", 9.5f)
        };

        _btnBackupAddFolder = new Button
        {
            Text = "➕ Klasör Ekle",
            Location = new Point(695, 145),
            Size = new Size(125, 38),
            BackColor = Color.FromArgb(37, 99, 235),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        _btnBackupAddFolder.Click += (_, _) => AddBackupFolder();

        _btnBackupRemoveFolder = new Button
        {
            Text = "Kaldır",
            Location = new Point(695, 192),
            Size = new Size(125, 35),
            FlatStyle = FlatStyle.Flat
        };
        _btnBackupRemoveFolder.Click += (_, _) =>
        {
            if (_lstBackupFolders.SelectedIndex >= 0) _lstBackupFolders.Items.RemoveAt(_lstBackupFolders.SelectedIndex);
        };

        _lblBackupStatus = new Label
        {
            Text = "Hub yedekleme planı kontrol ediliyor...",
            Location = new Point(20, 395),
            Size = new Size(800, 45),
            ForeColor = Color.FromArgb(71, 85, 105)
        };

        _btnBackupSave = new Button
        {
            Text = "💾 Yedekleme Ayarını Kaydet",
            Location = new Point(20, 455),
            Size = new Size(235, 42),
            BackColor = Color.FromArgb(16, 185, 129),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        _btnBackupSave.Click += async (_, _) => await SaveBackupPlanAsync();

        _btnBackupRunNow = new Button
        {
            Text = "▶ Şimdi Yedekle",
            Location = new Point(270, 455),
            Size = new Size(180, 42),
            BackColor = Color.FromArgb(79, 70, 229),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        _btnBackupRunNow.Click += async (_, _) => await RunBackupNowAsync();

        var safety = new Label
        {
            Text = "Koruma: 500+ dosya, en az 100 dosyalık sette %15+ değişiklik veya 100+ şüpheli yeniden adlandırma görülürse yedekleme başlamadan durur. Kaynak silme Dropbox kopyasını silmez.",
            Location = new Point(20, 520),
            Size = new Size(790, 50),
            ForeColor = Color.FromArgb(146, 64, 14)
        };

        tab.Controls.AddRange(new Control[]
        {
            title, description, _chkBackupEnabled, hourLabel, _numBackupHour,
            _lstBackupFolders, _btnBackupAddFolder, _btnBackupRemoveFolder,
            _lblBackupStatus, _btnBackupSave, _btnBackupRunNow, safety
        });
        return tab;
    }

    private TabPage CreateServiceTab()
    {
        var tab = new TabPage("🛡️ Windows Servis Modu");
        tab.Padding = new Padding(25);
        tab.BackColor = Color.White;

        var lblTitle = new Label
        {
            Text = "Windows Arka Plan Servisi Olarak Çalıştırma",
            Font = new Font("Segoe UI", 12.0f, FontStyle.Bold),
            ForeColor = Color.FromArgb(15, 23, 42),
            Location = new Point(20, 20),
            AutoSize = true
        };

        var lblDesc = new Label
        {
            Text = "Uygulama Windows Servisi olarak kurulduğunda sunucu yeniden başlatılsa ve hiçbir kullanıcı oturum açmasa bile " +
                   "arka planda otomatik çalışmaya başlar, diskleri düzenli aralıklarla izler, VDA Hub'a veri aktarır ve " +
                   "Brevo üzerinden günlük rapor ile kritik alarmları eksiksiz iletir.",
            Font = new Font("Segoe UI", 9.5f, FontStyle.Regular),
            ForeColor = Color.FromArgb(71, 85, 105),
            Location = new Point(20, 55),
            Size = new Size(780, 50)
        };

        var pnlStatusBox = new Panel
        {
            Location = new Point(20, 115),
            Size = new Size(780, 70),
            BackColor = Color.FromArgb(241, 245, 249),
            BorderStyle = BorderStyle.FixedSingle
        };

        var lblStatusTitle = new Label
        {
            Text = "Mevcut Servis Durumu:",
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            Location = new Point(15, 12),
            AutoSize = true
        };

        _lblServiceStatus = new Label
        {
            Text = "Kontrol ediliyor...",
            Font = new Font("Segoe UI", 11.0f, FontStyle.Bold),
            ForeColor = Color.FromArgb(37, 99, 235),
            Location = new Point(15, 34),
            AutoSize = true
        };
        pnlStatusBox.Controls.AddRange(new Control[] { lblStatusTitle, _lblServiceStatus });

        _btnInstallService = new Button
        {
            Text = "📥 Servisi Kur (Otomatik Başlat)",
            Location = new Point(20, 200),
            Size = new Size(230, 42),
            BackColor = Color.FromArgb(16, 185, 129),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        _btnInstallService.FlatAppearance.BorderSize = 0;
        _btnInstallService.Click += (s, e) => InstallService();

        _btnStartService = new Button
        {
            Text = "▶ Servisi Başlat",
            Location = new Point(265, 200),
            Size = new Size(160, 42),
            BackColor = Color.FromArgb(37, 99, 235),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        _btnStartService.FlatAppearance.BorderSize = 0;
        _btnStartService.Click += (s, e) => StartService();

        _btnStopService = new Button
        {
            Text = "⏹ Servisi Durdur",
            Location = new Point(440, 200),
            Size = new Size(160, 42),
            BackColor = Color.FromArgb(245, 158, 11),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        _btnStopService.FlatAppearance.BorderSize = 0;
        _btnStopService.Click += (s, e) => StopService();

        _btnUninstallService = new Button
        {
            Text = "🗑 Servisi Kaldır",
            Location = new Point(615, 200),
            Size = new Size(180, 42),
            BackColor = Color.FromArgb(239, 68, 68),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        _btnUninstallService.FlatAppearance.BorderSize = 0;
        _btnUninstallService.Click += (s, e) => UninstallService();

        tab.Controls.AddRange(new Control[] {
            lblTitle, lblDesc, pnlStatusBox,
            _btnInstallService, _btnStartService, _btnStopService, _btnUninstallService
        });

        return tab;
    }

    private TabPage CreateLogsTab()
    {
        var tab = new TabPage("📜 Canlı Loglar");
        tab.Padding = new Padding(15);
        tab.BackColor = Color.White;

        var pnlTop = new Panel
        {
            Dock = DockStyle.Top,
            Height = 40
        };

        var btnClear = new Button
        {
            Text = "Logları Temizle",
            Location = new Point(0, 5),
            Size = new Size(120, 30),
            BackColor = Color.FromArgb(241, 245, 249),
            FlatStyle = FlatStyle.Flat
        };
        btnClear.Click += (s, e) => _txtLogs.Clear();
        pnlTop.Controls.Add(btnClear);

        _txtLogs = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            ReadOnly = true,
            BackColor = Color.FromArgb(15, 23, 42),
            ForeColor = Color.FromArgb(226, 232, 240),
            Font = new Font("Consolas", 9.0f)
        };

        tab.Controls.Add(_txtLogs);
        tab.Controls.Add(pnlTop);

        return tab;
    }

    private void SetupEvents()
    {
        _engine.OnLog += msg =>
        {
            if (this.IsDisposed) return;
            if (this.InvokeRequired)
            {
                this.BeginInvoke(new Action(() => AppendLog(msg)));
            }
            else
            {
                AppendLog(msg);
            }
        };

        _engine.OnReportUpdated += report =>
        {
            if (this.IsDisposed) return;
            if (this.InvokeRequired)
            {
                this.BeginInvoke(new Action(() => UpdateDashboard(report)));
            }
            else
            {
                UpdateDashboard(report);
            }
        };
    }

    private async Task LoadBackupPlanAsync()
    {
        try
        {
            var plan = await _backupService.GetPlanAsync();
            _lstBackupFolders.Items.Clear();
            if (plan is null)
            {
                _lblBackupStatus.Text = "Hub yedekleme planı okunamadı. Hub URL/API anahtarını kontrol edin.";
                return;
            }

            foreach (var path in plan.Paths) _lstBackupFolders.Items.Add(path);
            _chkBackupEnabled.Checked = plan.Enabled;
            _numBackupHour.Value = Math.Clamp(plan.RunHourLocal, 0, 23);
            _lblBackupStatus.Text = plan.Configured
                ? (plan.Ready ? "Hazır · Dropbox bağlantısı ve sunucu planı aktif." : plan.Message ?? "Plan kayıtlı; yedekleme şu an hazır değil.")
                : "Önce Hub / Sunucular ekranından bu sunucu için yeni kurulum paketi üretin.";
        }
        catch (Exception ex)
        {
            _lblBackupStatus.Text = "Yedekleme planı alınamadı: " + ex.Message;
        }
    }

    private void AddBackupFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Dropbox'a yedeklenecek klasörü seçin",
            ShowNewFolderButton = false,
            UseDescriptionForTitle = true
        };
        if (dialog.ShowDialog(this) != DialogResult.OK || string.IsNullOrWhiteSpace(dialog.SelectedPath)) return;
        if (!_lstBackupFolders.Items.Cast<string>().Contains(dialog.SelectedPath, StringComparer.OrdinalIgnoreCase))
            _lstBackupFolders.Items.Add(dialog.SelectedPath);
    }

    private async Task SaveBackupPlanAsync()
    {
        _btnBackupSave.Enabled = false;
        try
        {
            var paths = _lstBackupFolders.Items.Cast<string>().ToArray();
            var (success, message) = await _backupService.SavePlanAsync(
                _chkBackupEnabled.Checked,
                paths,
                (int)_numBackupHour.Value);
            _lblBackupStatus.Text = message;
            MessageBox.Show(message, success ? "Başarılı" : "Yedekleme", MessageBoxButtons.OK,
                success ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            if (success) await LoadBackupPlanAsync();
        }
        finally
        {
            _btnBackupSave.Enabled = true;
        }
    }

    private async Task RunBackupNowAsync()
    {
        _btnBackupRunNow.Enabled = false;
        _lblBackupStatus.Text = "Yedekleme kontrol ediliyor...";
        try
        {
            var result = await _backupService.RunIfDueAsync(force: true);
            _lblBackupStatus.Text = result.Message;
            MessageBox.Show(result.Message,
                result.Success ? "Yedekleme tamamlandı" : (result.RequiresApproval ? "Güvenlik kilidi" : "Yedekleme hatası"),
                MessageBoxButtons.OK,
                result.Success ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
        finally
        {
            _btnBackupRunNow.Enabled = true;
        }
    }

    private void AppendLog(string message)
    {
        if (_txtLogs.IsDisposed) return;
        _txtLogs.AppendText(message + Environment.NewLine);
    }

    private void UpdateDashboard(ServerStatusReport report)
    {
        _lblServerTitle.Text = $"VDA HUB: {report.ServerName}";
        _lblServerMeta.Text = $"Sunucu: {report.MachineName} • İşletim Sistemi: {report.OsVersion} • Son Güncelleme: {DateTime.Now:HH:mm:ss}";

        switch (report.OverallStatus)
        {
            case "CRITICAL":
                _lblStatusBadge.Text = "⛔ KRİTİK SEVİYE";
                _lblStatusBadge.BackColor = Color.FromArgb(239, 68, 68);
                break;
            case "WARNING":
                _lblStatusBadge.Text = "⚠ DİKKAT (UYARI)";
                _lblStatusBadge.BackColor = Color.FromArgb(245, 158, 11);
                break;
            default:
                _lblStatusBadge.Text = "✓ SAĞLIKLI";
                _lblStatusBadge.BackColor = Color.FromArgb(16, 185, 129);
                break;
        }

        // Update volumes list
        _lvVolumes.Items.Clear();
        foreach (var vol in report.Volumes)
        {
            var item = new ListViewItem(vol.Name);
            item.SubItems.Add(vol.VolumeLabel);
            item.SubItems.Add($"%{vol.UsedPercentage:F1}");
            item.SubItems.Add($"{vol.UsedSizeGb} GB");
            item.SubItems.Add($"{vol.FreeSizeGb} GB");
            item.SubItems.Add($"{vol.TotalSizeGb} GB");
            item.SubItems.Add(vol.DriveFormat);
            item.SubItems.Add(vol.HealthStatus);

            if (vol.HealthStatus == "CRITICAL")
                item.BackColor = Color.FromArgb(254, 226, 226);
            else if (vol.HealthStatus == "WARNING")
                item.BackColor = Color.FromArgb(254, 243, 199);

            _lvVolumes.Items.Add(item);
        }

        // Update physical disks list
        _lvPhysicalDisks.Items.Clear();
        foreach (var phys in report.PhysicalDisks)
        {
            var item = new ListViewItem(phys.DeviceId);
            item.SubItems.Add(phys.Model);
            item.SubItems.Add($"{phys.InterfaceType} / {phys.MediaType}");
            item.SubItems.Add($"{phys.SizeGb} GB");
            item.SubItems.Add(phys.Status);

            if (!phys.Status.Equals("OK", StringComparison.OrdinalIgnoreCase))
                item.BackColor = Color.FromArgb(254, 226, 226);

            _lvPhysicalDisks.Items.Add(item);
        }

        _lblSummary.Text = $"Özet: {report.SummaryMessage}\nToplam Mantıksal Bölüm: {report.Volumes.Count} | Fiziksel Disk: {report.PhysicalDisks.Count}";
    }

    private void LoadSettingsIntoUI()
    {
        var config = _configService.Current;
        _txtServerName.Text = config.ServerName;
        _numInterval.Value = Math.Max(1, config.CheckIntervalMinutes);

        // Hub
        _chkHubEnabled.Checked = config.Hub.Enabled;
        _txtHubUrl.Text = config.Hub.HubApiUrl;
        _txtHubApiKey.Text = config.Hub.ApiKey;

        // Brevo
        _chkBrevoEnabled.Checked = config.Brevo.Enabled;
        _txtBrevoApiKey.Text = config.Brevo.ApiKey;
        _txtBrevoSenderEmail.Text = config.Brevo.SenderEmail;
        _txtBrevoSenderName.Text = config.Brevo.SenderName;
        _txtBrevoRecipients.Text = config.Brevo.RecipientEmails;
        _txtBrevoDailyTime.Text = config.Brevo.DailyReportTime;
        _chkBrevoCriticalAlert.Checked = config.Brevo.SendCriticalAlertImmediately;
        _numCriticalThreshold.Value = Math.Clamp(config.Brevo.CriticalThresholdPercent, 50, 99);
    }

    private void SaveSettingsFromUI()
    {
        var config = _configService.Current;
        config.ServerName = _txtServerName.Text.Trim();
        config.CheckIntervalMinutes = (int)_numInterval.Value;

        // Hub
        config.Hub.Enabled = _chkHubEnabled.Checked;
        config.Hub.HubApiUrl = _txtHubUrl.Text.Trim();
        config.Hub.ApiKey = _txtHubApiKey.Text.Trim();

        // Brevo
        config.Brevo.Enabled = _chkBrevoEnabled.Checked;
        config.Brevo.ApiKey = _txtBrevoApiKey.Text.Trim();
        config.Brevo.SenderEmail = _txtBrevoSenderEmail.Text.Trim();
        config.Brevo.SenderName = _txtBrevoSenderName.Text.Trim();
        config.Brevo.RecipientEmails = _txtBrevoRecipients.Text.Trim();
        config.Brevo.DailyReportTime = _txtBrevoDailyTime.Text.Trim();
        config.Brevo.SendCriticalAlertImmediately = _chkBrevoCriticalAlert.Checked;
        config.Brevo.CriticalThresholdPercent = (int)_numCriticalThreshold.Value;

        _configService.Save();
        MessageBox.Show("Ayarlar başarıyla kaydedildi.", "Bilgi", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private async Task TriggerManualScan()
    {
        _btnScanNow.Enabled = false;
        try
        {
            await _engine.ExecuteCheckCycleAsync(forceHub: _configService.Current.Hub.Enabled);
        }
        finally
        {
            _btnScanNow.Enabled = true;
        }
    }

    private async Task TriggerTestEmail()
    {
        var brevo = _configService.Current.Brevo;
        if (string.IsNullOrWhiteSpace(_txtBrevoApiKey.Text) && string.IsNullOrWhiteSpace(brevo.ApiKey))
        {
            MessageBox.Show("Lütfen önce Brevo API Key girin.", "Uyarı", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // Test with current input values
        var testSettings = new BrevoSettings
        {
            ApiKey = _txtBrevoApiKey.Text.Trim(),
            SenderEmail = _txtBrevoSenderEmail.Text.Trim(),
            SenderName = _txtBrevoSenderName.Text.Trim(),
            RecipientEmails = _txtBrevoRecipients.Text.Trim()
        };

        _btnTestBrevo.Enabled = false;
        try
        {
            var (success, msg) = await _brevoService.SendTestEmailAsync(testSettings, _txtServerName.Text.Trim());
            if (success)
            {
                MessageBox.Show("Brevo test e-postası başarıyla gönderildi!\nLütfen gelen kutunuzu kontrol edin.", "Başarılı", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                MessageBox.Show($"Test e-postası gönderilemedi:\n{msg}", "Hata", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        finally
        {
            _btnTestBrevo.Enabled = true;
        }
    }

    private async Task TriggerTestHub()
    {
        if (string.IsNullOrWhiteSpace(_txtHubUrl.Text))
        {
            MessageBox.Show("Lütfen VDA Hub API URL adresini girin.", "Uyarı", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var testSettings = new HubSettings
        {
            Enabled = true,
            HubApiUrl = _txtHubUrl.Text.Trim(),
            ApiKey = _txtHubApiKey.Text.Trim()
        };

        _btnTestHub.Enabled = false;
        try
        {
            var report = _engine.LastReport ?? new DiskMonitorService().CollectReport(_configService.Current);
            var (success, msg, statusCode) = await _hubClient.SendReportAsync(testSettings, report);
            if (success)
            {
                MessageBox.Show($"VDA Hub bağlantısı başarılı! (Status: {statusCode})\n{msg}", "Başarılı", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                MessageBox.Show($"VDA Hub bağlantısı başarısız:\n{msg}", "Hata", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        finally
        {
            _btnTestHub.Enabled = true;
        }
    }

    private void RefreshServiceStatus()
    {
        string status = WindowsServiceManager.GetServiceStatus();
        _lblServiceStatus.Text = status;

        bool isInstalled = WindowsServiceManager.IsServiceInstalled();
        _btnInstallService.Enabled = !isInstalled;
        _btnUninstallService.Enabled = isInstalled;
        _btnStartService.Enabled = isInstalled && !status.Contains("RUNNING");
        _btnStopService.Enabled = isInstalled && status.Contains("RUNNING");
    }

    private void InstallService()
    {
        string exePath = Process.GetCurrentProcess().MainModule?.FileName ?? Application.ExecutablePath;
        var (success, output) = WindowsServiceManager.InstallService(exePath);
        MessageBox.Show(output, success ? "Başarılı" : "Hata", MessageBoxButtons.OK, success ? MessageBoxIcon.Information : MessageBoxIcon.Error);
        RefreshServiceStatus();
    }

    private void UninstallService()
    {
        var (success, output) = WindowsServiceManager.UninstallService();
        MessageBox.Show(output, success ? "Başarılı" : "Hata", MessageBoxButtons.OK, success ? MessageBoxIcon.Information : MessageBoxIcon.Error);
        RefreshServiceStatus();
    }

    private void StartService()
    {
        var (success, output) = WindowsServiceManager.StartService();
        MessageBox.Show(output, success ? "Başarılı" : "Hata", MessageBoxButtons.OK, success ? MessageBoxIcon.Information : MessageBoxIcon.Error);
        RefreshServiceStatus();
    }

    private void StopService()
    {
        var (success, output) = WindowsServiceManager.StopService();
        MessageBox.Show(output, success ? "Başarılı" : "Hata", MessageBoxButtons.OK, success ? MessageBoxIcon.Information : MessageBoxIcon.Error);
        RefreshServiceStatus();
    }

    private void MinimizeToTray()
    {
        this.Hide();
        _trayIcon.ShowBalloonTip(3000, "VDA Hub Server Controller", "Uygulama arka planda sistem tepsisinde çalışmaya devam ediyor.", ToolTipIcon.Info);
    }

    private void ShowAndRestore()
    {
        this.Show();
        this.WindowState = FormWindowState.Normal;
        this.BringToFront();
    }

    private void ExitApplication()
    {
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        Application.Exit();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            MinimizeToTray();
        }
        else
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            base.OnFormClosing(e);
        }
    }
}
