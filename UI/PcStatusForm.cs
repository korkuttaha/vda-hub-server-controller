using System.Drawing;
using System.Windows.Forms;
using VdaHubServerController.Services;

namespace VdaHubServerController.UI;

public sealed class PcStatusForm : Form
{
    private readonly PcConfigService _config;
    private readonly Label _serviceStatusLabel;

    public PcStatusForm(PcConfigService config)
    {
        _config = config;
        Text = "VDAKor PC Agent · Durum";
        Size = new Size(620, 420);
        MinimumSize = MaximumSize = Size;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9.5f);
        BackColor = Color.White;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;

        var title = new Label
        {
            Text = "VDAKor PC Agent Çalışıyor",
            Font = new Font("Segoe UI", 14f, FontStyle.Bold),
            ForeColor = Color.FromArgb(15, 23, 42),
            Location = new Point(28, 24),
            AutoSize = true
        };
        var desc = new Label
        {
            Text = "Bu bilgisayar VDAKor Hub'a bağlıdır ve arka planda Windows servisi olarak komutları dinlemektedir.",
            ForeColor = Color.FromArgb(71, 85, 105),
            Location = new Point(28, 60),
            Size = new Size(540, 40)
        };
        Controls.Add(title);
        Controls.Add(desc);

        var card = new Panel
        {
            Location = new Point(28, 110),
            Size = new Size(548, 175),
            BackColor = Color.FromArgb(248, 250, 252),
            BorderStyle = BorderStyle.FixedSingle
        };

        var lblPcTitle = new Label { Text = "Bilgisayar Adı:", Location = new Point(16, 16), AutoSize = true, ForeColor = Color.FromArgb(100, 116, 139), Font = new Font("Segoe UI", 9f, FontStyle.Bold) };
        var lblPcValue = new Label { Text = _config.Current.ComputerName, Location = new Point(16, 36), AutoSize = true, ForeColor = Color.FromArgb(15, 23, 42), Font = new Font("Segoe UI", 10.5f, FontStyle.Bold) };

        var lblHubTitle = new Label { Text = "Bağlı Hub Adresi:", Location = new Point(16, 68), AutoSize = true, ForeColor = Color.FromArgb(100, 116, 139), Font = new Font("Segoe UI", 9f, FontStyle.Bold) };
        var lblHubValue = new Label { Text = _config.Current.HubBaseUrl, Location = new Point(16, 88), AutoSize = true, ForeColor = Color.FromArgb(15, 23, 42), Font = new Font("Segoe UI", 9.5f) };

        var lblServiceTitle = new Label { Text = "Windows Servis Durumu:", Location = new Point(16, 120), AutoSize = true, ForeColor = Color.FromArgb(100, 116, 139), Font = new Font("Segoe UI", 9f, FontStyle.Bold) };
        _serviceStatusLabel = new Label { Text = PcWindowsServiceManager.GetServiceStatus(), Location = new Point(175, 120), AutoSize = true, ForeColor = Color.FromArgb(5, 150, 105), Font = new Font("Segoe UI", 9.5f, FontStyle.Bold) };

        card.Controls.Add(lblPcTitle);
        card.Controls.Add(lblPcValue);
        card.Controls.Add(lblHubTitle);
        card.Controls.Add(lblHubValue);
        card.Controls.Add(lblServiceTitle);
        card.Controls.Add(_serviceStatusLabel);
        Controls.Add(card);

        var reEnrollBtn = new Button
        {
            Text = "Yeni Kod ile Yeniden Eşleştir",
            Location = new Point(28, 310),
            Size = new Size(240, 42),
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
            Location = new Point(280, 310),
            Size = new Size(170, 42),
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
            Location = new Point(462, 310),
            Size = new Size(114, 42),
            FlatStyle = FlatStyle.Flat
        };
        closeBtn.Click += (_, _) => Close();
        Controls.Add(closeBtn);

        CancelButton = closeBtn;
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
