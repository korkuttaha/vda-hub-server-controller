using System.Drawing;
using System.Windows.Forms;
using VdaHubServerController.Services;

namespace VdaHubServerController.UI;

public sealed class EnrollmentForm : Form
{
    private readonly EnrollmentService _enrollment;
    private readonly TextBox _txtHubUrl;
    private readonly TextBox _txtSetupKey;
    private readonly Label _status;
    private readonly Button _connect;

    public EnrollmentForm(EnrollmentService enrollment)
    {
        _enrollment = enrollment;

        Text = "VDA Hub · İlk Kurulum";
        Size = new Size(620, 360);
        MinimumSize = new Size(620, 360);
        MaximumSize = new Size(620, 360);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9.5f);
        BackColor = Color.White;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;

        var title = new Label
        {
            Text = "Bu sunucuyu VDA Hub'a bağlayın",
            Font = new Font("Segoe UI", 14f, FontStyle.Bold),
            ForeColor = Color.FromArgb(15, 23, 42),
            Location = new Point(28, 24),
            AutoSize = true
        };

        var desc = new Label
        {
            Text = "Önce Hub > Sunucular ekranında sunucu kaydını oluşturup tek kullanımlık Kurulum Anahtarını alın. Bu EXE tüm sunucularda aynıdır; kimlik eşleştirmesi anahtarla yapılır.",
            ForeColor = Color.FromArgb(71, 85, 105),
            Location = new Point(28, 60),
            Size = new Size(540, 48)
        };

        var hubLabel = new Label { Text = "Hub adresi", Location = new Point(28, 122), AutoSize = true };
        _txtHubUrl = new TextBox
        {
            Location = new Point(28, 145),
            Size = new Size(540, 27),
            Text = enrollment.SuggestedHubBaseUrl()
        };

        var keyLabel = new Label { Text = "Kurulum Anahtarı", Location = new Point(28, 188), AutoSize = true };
        _txtSetupKey = new TextBox
        {
            Location = new Point(28, 211),
            Size = new Size(540, 27),
            PlaceholderText = "vda_setup_...",
            UseSystemPasswordChar = true
        };

        _status = new Label
        {
            Location = new Point(28, 247),
            Size = new Size(540, 34),
            ForeColor = Color.FromArgb(71, 85, 105),
            Text = "Anahtar 24 saat geçerlidir ve ilk başarılı bağlantıda tüketilir."
        };

        _connect = new Button
        {
            Text = "Hub'a Bağla",
            Location = new Point(398, 286),
            Size = new Size(170, 38),
            BackColor = Color.FromArgb(37, 99, 235),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            DialogResult = DialogResult.None
        };
        _connect.FlatAppearance.BorderSize = 0;
        _connect.Click += async (_, _) => await ConnectAsync();

        var cancel = new Button
        {
            Text = "Kapat",
            Location = new Point(286, 286),
            Size = new Size(100, 38),
            FlatStyle = FlatStyle.Flat
        };
        cancel.Click += (_, _) => Close();

        Controls.AddRange(new Control[]
        {
            title, desc, hubLabel, _txtHubUrl, keyLabel, _txtSetupKey, _status, cancel, _connect
        });

        AcceptButton = _connect;
        CancelButton = cancel;
    }

    private async Task ConnectAsync()
    {
        _connect.Enabled = false;
        _status.ForeColor = Color.FromArgb(71, 85, 105);
        _status.Text = "Hub ile güvenli eşleştirme yapılıyor...";
        try
        {
            var (success, message) = await _enrollment.EnrollAsync(_txtHubUrl.Text, _txtSetupKey.Text);
            _status.Text = message;
            _status.ForeColor = success ? Color.FromArgb(5, 150, 105) : Color.FromArgb(220, 38, 38);
            if (!success) return;

            MessageBox.Show(
                message + "\n\nKalıcı API anahtarı arka planda kaydedildi. Artık normal ajan ekranına geçebilirsiniz.",
                "Eşleştirme tamamlandı",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            DialogResult = DialogResult.OK;
            Close();
        }
        finally
        {
            _connect.Enabled = true;
        }
    }
}
