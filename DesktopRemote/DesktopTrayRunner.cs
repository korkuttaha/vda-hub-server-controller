using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.Win32;
using VdaHubServerController.Services;

namespace VdaHubServerController.DesktopRemote;

public sealed class DesktopTrayRunner : ApplicationContext
{
    private const string RunRegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "VDAKorDesktopRemote";

    private readonly DesktopRemoteServer _server;
    private readonly NotifyIcon _trayIcon;
    private readonly ToolStripMenuItem _autoStartItem;
    private readonly int _port;

    public DesktopTrayRunner(DesktopRemoteServer server, int port)
    {
        _server = server;
        _port = port;

        _autoStartItem = new ToolStripMenuItem("🚀 Windows ile Birlikte Başlat", null, ToggleAutoStart)
        {
            Checked = IsAutoStartEnabled()
        };

        var menu = new ContextMenuStrip();
        var titleItem = new ToolStripMenuItem($"🖥️ VDAKor Masaüstü Kumandası ({_port})")
        {
            Font = new Font(menu.Font, FontStyle.Bold),
            Enabled = false
        };
        menu.Items.Add(titleItem);
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add("🌐 Web Kumandayı Aç", null, (_, _) => OpenWebClient());
        menu.Items.Add("📱 Açık Programları Göster", null, (_, _) => ShowAppsDialog());
        menu.Items.Add(_autoStartItem);
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add("❌ Kapat ve Çık", null, (_, _) => Exit());

        // Default icon
        Icon? icon = null;
        try
        {
            icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? Application.ExecutablePath);
        }
        catch { }

        _trayIcon = new NotifyIcon
        {
            Icon = icon ?? SystemIcons.Application,
            ContextMenuStrip = menu,
            Text = $"VDAKor Desktop Remote (Port {_port})",
            Visible = true
        };

        _trayIcon.DoubleClick += (_, _) => OpenWebClient();

        _trayIcon.ShowBalloonTip(
            3000,
            "VDAKor Desktop Remote Aktif",
            $"Masaüstü kumanda sunucusu port {_port} üzerinde dinleniyor.\nTelefonunuzdan erişmek için çift tıklayın.",
            ToolTipIcon.Info
        );
    }

    private void OpenWebClient()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = $"http://localhost:{_port}",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Tarayıcı açılamadı: {ex.Message}", "Hata", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

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
}
