using System.Drawing;
using System.Windows.Forms;
using VdaHubServerController.Services;

namespace VdaHubServerController.UI;

public sealed class PcEnrollmentForm : Form
{
    private readonly PcEnrollmentService _enrollment;
    private readonly TextBox _hub;
    private readonly TextBox _key;
    private readonly Label _status;
    private readonly Button _connect;

    public PcEnrollmentForm(PcEnrollmentService enrollment)
    {
        _enrollment = enrollment;
        Text = "VDAKor PC Agent · İlk Kurulum";
        Size = new Size(620, 360);
        MinimumSize = MaximumSize = Size;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9.5f);
        BackColor = Color.White;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;

        var title = new Label
        {
            Text = "Bu bilgisayarı VDAKor Hub'a bağla",
            Font = new Font("Segoe UI", 14f, FontStyle.Bold),
            ForeColor = Color.FromArgb(15, 23, 42),
            Location = new Point(28, 24),
            AutoSize = true
        };
        var desc = new Label
        {
            Text = "Hub > Bilgisayarlar ekranında oluşturduğun tek kullanımlık eşleştirme kodunu gir. Bu uygulama sunucu ajanından bağımsızdır.",
            ForeColor = Color.FromArgb(71, 85, 105),
            Location = new Point(28, 60),
            Size = new Size(540, 48)
        };
        Controls.Add(title);
        Controls.Add(desc);
        Controls.Add(new Label { Text = "Hub adresi", Location = new Point(28, 122), AutoSize = true });
        _hub = new TextBox { Location = new Point(28, 145), Size = new Size(540, 27), Text = enrollment.SuggestedHubBaseUrl() };
        Controls.Add(_hub);
        Controls.Add(new Label { Text = "Bilgisayar eşleştirme kodu", Location = new Point(28, 188), AutoSize = true });
        _key = new TextBox { Location = new Point(28, 211), Size = new Size(540, 27), PlaceholderText = "vda_pc_setup_...", UseSystemPasswordChar = true };
        Controls.Add(_key);
        _status = new Label { Location = new Point(28, 247), Size = new Size(540, 34), ForeColor = Color.FromArgb(71, 85, 105), Text = "Kod 24 saat geçerlidir ve tek kullanımlıdır." };
        Controls.Add(_status);

        var cancel = new Button { Text = "Kapat", Location = new Point(286, 286), Size = new Size(100, 38), FlatStyle = FlatStyle.Flat };
        cancel.Click += (_, _) => Close();
        Controls.Add(cancel);

        _connect = new Button { Text = "Bilgisayarı Bağla", Location = new Point(398, 286), Size = new Size(170, 38), BackColor = Color.FromArgb(37,99,235), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
        _connect.FlatAppearance.BorderSize = 0;
        _connect.Click += async (_, _) => await ConnectAsync();
        Controls.Add(_connect);

        AcceptButton = _connect;
        CancelButton = cancel;
    }

    private async Task ConnectAsync()
    {
        _connect.Enabled = false;
        _status.Text = "Hub ile güvenli eşleştirme yapılıyor...";
        try
        {
            var (success, message) = await _enrollment.EnrollAsync(_hub.Text, _key.Text);
            _status.Text = message;
            _status.ForeColor = success ? Color.FromArgb(5,150,105) : Color.FromArgb(220,38,38);
            if (!success) return;
            MessageBox.Show(message + "\n\nVDAKor PC Agent Windows servisi otomatik kurulacak.", "Eşleştirme tamamlandı", MessageBoxButtons.OK, MessageBoxIcon.Information);
            DialogResult = DialogResult.OK;
            Close();
        }
        finally
        {
            _connect.Enabled = true;
        }
    }
}
