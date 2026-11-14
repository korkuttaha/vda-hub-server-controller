using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.Win32;
using VdaHubServerController.DesktopRemote;
using VdaHubServerController.Services;

namespace VdaHubServerController.UI;

public sealed class PcStatusForm : Form
{
    private const string RunRegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "VDAKorDesktopRemote";

    private readonly PcConfigService _config;
    private readonly Label _serviceStatusLabel;

    public PcStatusForm(PcConfigService config)
    {
        _config = config;
        var version = typeof(PcStatusForm).Assembly.GetName().Version?.ToString(3) ?? "—";
        Text = $"VDAKor PC Agent · v{version}";
        Size = new Size(620, 560);
        MinimumSize = MaximumSize = Size;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9.5f);
        BackColor = Color.White;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;

        var title = new Label
        {
            Text = $"VDAKor PC Agent & Masaüstü Kumandası · v{version}",
            Font = new Font("Segoe UI", 13.5f, FontStyle.Bold),
            ForeColor = Color.FromArgb(15, 23, 42),
            Location = new Point(28, 20),
            AutoSize = true
        };
        var desc = new Label
        {
            Text = "Bu bilgisayar VDAKor Hub'a bağlıdır ve telefondan uzaktan kumanda komutlarını dinlemektedir.",
            ForeColor = Color.FromArgb(71, 85, 105),
            Location = new Point(28, 52),
            Size = new Size(540, 36)
        };
        Controls.Add(title);
        Controls.Add(desc);

        // 1. Hub Service Info Card
        var card = new Panel
        {
            Location = new Point(28, 95),
            Size = new Size(548, 160),
            BackColor = Color.FromArgb(248, 250, 252),
            BorderStyle = BorderStyle.FixedSingle
        };

        var lblPcTitle = new Label { Text = "Bilgisayar Adı:", Location = new Point(16, 12), AutoSize = true, ForeColor = Color.FromArgb(100, 116, 139), Font = new Font("Segoe UI", 8.5f, FontStyle.Bold) };
        var lblPcValue = new Label { Text = _config.Current.ComputerName, Location = new Point(16, 30), AutoSize = true, ForeColor = Color.FromArgb(15, 23, 42), Font = new Font("Segoe UI", 10f, FontStyle.Bold) };

        var lblHubTitle = new Label { Text = "Bağlı Hub Adresi:", Location = new Point(16, 58), AutoSize = true, ForeColor = Color.FromArgb(100, 116, 139), Font = new Font("Segoe UI", 8.5f, FontStyle.Bold) };
        var lblHubValue = new Label { Text = _config.Current.HubBaseUrl, Location = new Point(16, 76), AutoSize = true, ForeColor = Color.FromArgb(15, 23, 42), Font = new Font("Segoe UI", 9.5f) };

        var lblServiceTitle = new Label { Text = "Windows Servis Durumu:", Location = new Point(16, 106), AutoSize = true, ForeColor = Color.FromArgb(100, 116, 139), Font = new Font("Segoe UI", 8.5f, FontStyle.Bold) };
        _serviceStatusLabel = new Label { Text = PcWindowsServiceManager.GetServiceStatus(), Location = new Point(175, 106), AutoSize = true, ForeColor = Color.FromArgb(5, 150, 105), Font = new Font("Segoe UI", 9.5f, FontStyle.Bold) };

        card.Controls.Add(lblPcTitle);
        card.Controls.Add(lblPcValue);
        card.Controls.Add(lblHubTitle);
        card.Controls.Add(lblHubValue);
        card.Controls.Add(lblServiceTitle);
        card.Controls.Add(_serviceStatusLabel);
        Controls.Add(card);

        // 2. Desktop Remote Card
        var cardRemote = new Panel
        {
            Location = new Point(28, 268),
            Size = new Size(548, 175),
            BackColor = Color.FromArgb(240, 249, 255),
            BorderStyle = BorderStyle.FixedSingle
        };

        var lblRemoteTitle = new Label
        {
            Text = "🖥️ Masaüstü Kumandası",
            Font = new Font("Segoe UI", 10f, FontStyle.Bold),
            ForeColor = Color.FromArgb(2, 132, 199),
            Location = new Point(16, 12),
            AutoSize = true
        };

        var lblRemoteDesc = new Label
        {
            Text = "Telefonda VDAKor Mobile → Bilgisayarlar → Masaüstünü Yönet’i açın.",
            ForeColor = Color.FromArgb(51, 65, 85),
            Font = new Font("Segoe UI", 9f),
            Location = new Point(16, 36),
            Size = new Size(516, 36)
        };

        var autoStartChk = new CheckBox
        {
            Text = "Windows oturumu açıldığında masaüstü kumandasını başlat",
            Location = new Point(18, 76),
            Size = new Size(500, 24),
            Checked = IsAutoStartEnabled(),
            ForeColor = Color.FromArgb(30, 41, 59),
            Font = new Font("Segoe UI", 9f)
        };
        autoStartChk.CheckedChanged += (_, _) =>
        {
            DesktopTrayRunner.SetAutoStart(autoStartChk.Checked);
        };

        var remoteStartBtn = new Button
        {
            Text = "Masaüstü Kumandasını Başlat",
            Location = new Point(16, 112),
            Size = new Size(244, 42),
            BackColor = Color.FromArgb(2, 132, 199),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold)
        };
        remoteStartBtn.FlatAppearance.BorderSize = 0;
        remoteStartBtn.Click += (_, _) => OnStartRemoteClicked();

        cardRemote.Controls.Add(lblRemoteTitle);
        cardRemote.Controls.Add(lblRemoteDesc);
        cardRemote.Controls.Add(autoStartChk);
        cardRemote.Controls.Add(remoteStartBtn);
        Controls.Add(cardRemote);

        // Bottom Action Buttons
        var reEnrollBtn = new Button
        {
            Text = "Yeniden Eşleştir",
            Location = new Point(28, 460),
            Size = new Size(180, 42),
            BackColor = Color.FromArgb(220, 38, 38),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold)
        };
        reEnrollBtn.FlatAppearance.BorderSize = 0;
        reEnrollBtn.Click += (_, _) => OnReEnrollClicked();
        Controls.Add(reEnrollBtn);

        var restartBtn = new Button
        {
            Text = "Servisi Yeniden Başlat",
            Location = new Point(218, 460),
            Size = new Size(200, 42),
            FlatStyle = FlatStyle.Flat
        };
        restartBtn.Click += (_, _) =>
        {
            PcWindowsServiceManager.StopService();
            PcWindowsServiceManager.StartService();
            _serviceStatusLabel.Text = PcWindowsServiceManager.GetServiceStatus();
            MessageBox.Show("PC Agent servisi yeniden başlatıldı.", "Servis", MessageBoxButtons.OK, MessageBoxIcon.Information);
        };
        Controls.Add(restartBtn);

        var closeBtn = new Button
        {
            Text = "Kapat",
            Location = new Point(428, 460),
            Size = new Size(148, 42),
            FlatStyle = FlatStyle.Flat
        };
        closeBtn.Click += (_, _) => Close();
        Controls.Add(closeBtn);

        CancelButton = closeBtn;
    }

    private static void OnStartRemoteClicked()
    {
        try
        {
            string exe = Environment.ProcessPath ?? Application.ExecutablePath;
            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = "--desktop-server",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Masaüstü sunucusu başlatılamadı: {ex.Message}", "Hata", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
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

    private void OnReEnrollClicked()
    {
        var confirm = MessageBox.Show(
            "Mevcut eşleştirme ve arka plan servisi sıfırlanacak.\n\nHub'dan aldığınız yeni kod ile yeniden eşleştirmek istiyor musunuz?",
            "Yeniden Eşleştirme Onayı",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);

        if (confirm != DialogResult.Yes) return;

        PcWindowsServiceManager.ResetService();
        _config.Reset();
        DialogResult = DialogResult.Retry;
        Close();
    }
}
