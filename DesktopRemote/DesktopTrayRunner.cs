using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.Win32;

namespace VdaHubServerController.DesktopRemote;

public sealed class DesktopTrayRunner : ApplicationContext
{
    private const string RunRegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "VDAKorDesktopRemote";

    private readonly DesktopRemoteServer _server;
    private readonly NotifyIcon _trayIcon;
    private readonly ToolStripMenuItem _autoStartItem;

    public DesktopTrayRunner(DesktopRemoteServer server)
    {
        _server = server;

        _autoStartItem = new ToolStripMenuItem("🚀 Windows ile Birlikte Başlat", null, ToggleAutoStart)
        {
            Checked = IsAutoStartEnabled()
        };

        var menu = new ContextMenuStrip();
        var titleItem = new ToolStripMenuItem("🖥️ VDAKor Masaüstü Kumandası")
        {
            Font = new Font(menu.Font, FontStyle.Bold),
            Enabled = false
        };
        menu.Items.Add(titleItem);
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add("Bağlantı Bilgisi", null, (_, _) => ShowConnectionInfo());
        menu.Items.Add("📱 Açık Programları Göster", null, (_, _) => ShowAppsDialog());
        menu.Items.Add(_autoStartItem);
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add("❌ Kapat ve Çık", null, (_, _) => Exit());

        // Modern Tray Icon
        _trayIcon = new NotifyIcon
        {
            Icon = ResolveTrayIcon(),
            ContextMenuStrip = menu,
            Text = "VDAKor Masaüstü Kumandası",
            Visible = true
        };

        _trayIcon.DoubleClick += (_, _) => ShowConnectionInfo();

        _trayIcon.ShowBalloonTip(
            3000,
            "VDAKor Desktop Remote Aktif",
            "Telefonda Bilgisayarlar → Masaüstünü Yönet’i açın.",
            ToolTipIcon.Info
        );
    }

    private static void ShowConnectionInfo() => MessageBox.Show(
        "Telefonda VDAKor Mobile → Bilgisayarlar → Masaüstünü Yönet’i açın.",
        "VDAKor Masaüstü Kumandası", MessageBoxButtons.OK, MessageBoxIcon.Information);

    private void ShowAppsDialog()
    {
        var appsService = new DesktopAppManagerService();
        var apps = appsService.GetRunningApplications();
        string list = string.Join("\n", apps.Select(a => $"• [{a.ProcessId}] {a.WindowTitle}"));
        MessageBox.Show(
            string.IsNullOrWhiteSpace(list) ? "Açık pencere bulunamadı." : list,
            "Açık Pencereler",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information
        );
    }

    private void ToggleAutoStart(object? sender, EventArgs e)
    {
        bool currentState = IsAutoStartEnabled();
        SetAutoStart(!currentState);
        _autoStartItem.Checked = !currentState;
    }

    private static bool IsAutoStartEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunRegistryKey, false);
            return key?.GetValue(RunValueName) != null;
        }
        catch
        {
            return false;
        }
    }

    public static void SetAutoStart(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunRegistryKey, true);
            if (key == null) return;

            if (enable)
            {
                string exePath = Environment.ProcessPath ?? Application.ExecutablePath;
                key.SetValue(RunValueName, $"\"{exePath}\" --desktop-server");
            }
            else
            {
                key.DeleteValue(RunValueName, false);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[DesktopTrayRunner] Auto-start kaydı değiştirilemedi: {ex.Message}");
        }
    }

    private async void Exit()
    {
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        await _server.StopAsync();
        ExitThread();
    }

    private static Icon ResolveTrayIcon()
    {
        // 1. Gömülü manifest kaynağından yüklemeyi dene
        try
        {
            var asm = typeof(DesktopTrayRunner).Assembly;
            using var stream = asm.GetManifestResourceStream("VdaHubServerController.Assets.remote-desktop.ico");
            if (stream != null)
            {
                return new Icon(stream);
            }
        }
        catch { }

        // 2. Yanındaki Assets klasöründen yüklemeyi dene
        try
        {
            string baseDir = AppContext.BaseDirectory;
            string directFile = Path.Combine(baseDir, "Assets", "remote-desktop.ico");
            if (File.Exists(directFile))
            {
                return new Icon(directFile);
            }
        }
        catch { }

        // 3. Exe PE header'ındaki ikonu dene
        try
        {
            string? exe = Environment.ProcessPath ?? Application.ExecutablePath;
            if (!string.IsNullOrEmpty(exe) && File.Exists(exe))
            {
                var assoc = Icon.ExtractAssociatedIcon(exe);
                if (assoc != null) return assoc;
            }
        }
        catch { }

        // 4. Dinamik modern GDI+ çizimi
        return CreateFallbackModernIcon();
    }

    private static Icon CreateFallbackModernIcon()
    {
        try
        {
            using var bmp = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                using var penWhite = new Pen(Color.FromArgb(245, 245, 245), 2.2f)
                {
                    StartCap = System.Drawing.Drawing2D.LineCap.Round,
                    EndCap = System.Drawing.Drawing2D.LineCap.Round,
                    LineJoin = System.Drawing.Drawing2D.LineJoin.Round
                };

                // Monitör çerçevesi (Yuvarlatılmış)
                using var screenPath = new System.Drawing.Drawing2D.GraphicsPath();
                screenPath.AddArc(3.5f, 4f, 3f, 3f, 180, 90);
                screenPath.AddArc(25.5f, 4f, 3f, 3f, 270, 90);
                screenPath.AddArc(25.5f, 17f, 3f, 3f, 0, 90);
                screenPath.AddArc(3.5f, 17f, 3f, 3f, 90, 90);
                screenPath.CloseFigure();
                g.DrawPath(penWhite, screenPath);

                // Ayak ve taban
                g.DrawLine(penWhite, 16f, 20f, 16f, 27f);
                g.DrawLine(penWhite, 9f, 27f, 23f, 27f);

                // Cyan remote sinyal dalgası (#38BDF8)
                using var penCyan = new Pen(Color.FromArgb(56, 189, 248), 2.0f)
                {
                    StartCap = System.Drawing.Drawing2D.LineCap.Round,
                    EndCap = System.Drawing.Drawing2D.LineCap.Round
                };
                g.DrawArc(penCyan, 13f, 7f, 10f, 10f, 280, 80);
                g.FillEllipse(new SolidBrush(Color.FromArgb(56, 189, 248)), 14.5f, 14f, 2.8f, 2.8f);
            }
            IntPtr hIcon = bmp.GetHicon();
            return (Icon)Icon.FromHandle(hIcon).Clone();
        }
        catch
        {
            return SystemIcons.Application;
        }
    }

}
