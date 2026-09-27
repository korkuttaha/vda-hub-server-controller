using System.Drawing;
using System.Windows.Forms;

namespace VdaHubServerController.UI;

public sealed class FolderPathForm : Form
{
    private sealed record FolderEntry(string Name, string FullPath)
    {
        public override string ToString() => Name;
    }

    private readonly TextBox _path;
    private readonly ListBox _folders;
    private readonly Label _status;
    private string _currentPath = string.Empty;
    private int _loadGeneration;

    public string SelectedPath { get; private set; } = string.Empty;

    public FolderPathForm()
    {
        Text = "Yedeklenecek klasörü seçin";
        Size = new Size(700, 520);
        MinimumSize = new Size(700, 520);
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 9.5f);
        BackColor = Color.White;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;

        var description = new Label
        {
            Text = "Sunucudaki klasörlere çift tıklayarak ilerleyin veya tam yerel/ağ yolunu yazın. Listeleme arka planda çalışır; yavaş ya da bağlantısız bir disk pencereyi kilitlemez.",
            Location = new Point(24, 18),
            Size = new Size(635, 42),
            ForeColor = Color.FromArgb(71, 85, 105)
        };
        _path = new TextBox
        {
            Location = new Point(24, 68),
            Size = new Size(500, 27),
            PlaceholderText = @"C:\FirmaDosyalari veya \\sunucu\paylasim"
        };
        var go = new Button
        {
            Text = "Yola Git",
            Location = new Point(535, 66),
            Size = new Size(124, 31),
            FlatStyle = FlatStyle.Flat
        };
        go.Click += async (_, _) => await LoadLocationAsync(_path.Text);

        var drives = new Button
        {
            Text = "Sürücüler",
            Location = new Point(24, 108),
            Size = new Size(100, 32),
            FlatStyle = FlatStyle.Flat
        };
        drives.Click += async (_, _) => await LoadLocationAsync(null);
        var up = new Button
        {
            Text = "Üst Klasör",
            Location = new Point(136, 108),
            Size = new Size(110, 32),
            FlatStyle = FlatStyle.Flat
        };
        up.Click += async (_, _) => await LoadParentAsync();
        var open = new Button
        {
            Text = "Seçileni Aç",
            Location = new Point(258, 108),
            Size = new Size(120, 32),
            FlatStyle = FlatStyle.Flat
        };
        open.Click += async (_, _) => await OpenSelectedAsync();

        _folders = new ListBox
        {
            Location = new Point(24, 150),
            Size = new Size(635, 225),
            HorizontalScrollbar = true,
            Font = new Font("Segoe UI", 9.5f)
        };
        _folders.SelectedIndexChanged += (_, _) =>
        {
            if (_folders.SelectedItem is FolderEntry entry) _path.Text = entry.FullPath;
        };
        _folders.DoubleClick += async (_, _) => await OpenSelectedAsync();

        _status = new Label
        {
            Location = new Point(24, 384),
            Size = new Size(635, 35),
            ForeColor = Color.FromArgb(71, 85, 105)
        };

        var cancel = new Button
        {
            Text = "Vazgeç",
            Location = new Point(429, 429),
            Size = new Size(105, 36),
            FlatStyle = FlatStyle.Flat,
            DialogResult = DialogResult.Cancel
        };
        var select = new Button
        {
            Text = "Bu Klasörü Seç",
            Location = new Point(529, 429),
            Size = new Size(130, 36),
            BackColor = Color.FromArgb(37, 99, 235),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        select.FlatAppearance.BorderSize = 0;
        select.Click += (_, _) => AcceptPath();

        Controls.AddRange(new Control[]
        {
            description, _path, go, drives, up, open, _folders, _status, cancel, select
        });
        AcceptButton = select;
        CancelButton = cancel;
        Shown += async (_, _) => await LoadLocationAsync(null);
    }

    private async Task LoadParentAsync()
    {
        if (string.IsNullOrWhiteSpace(_currentPath)) return;
        var parent = Directory.GetParent(_currentPath)?.FullName;
        await LoadLocationAsync(parent);
    }

    private async Task OpenSelectedAsync()
    {
        if (_folders.SelectedItem is FolderEntry entry)
            await LoadLocationAsync(entry.FullPath);
    }

    private async Task LoadLocationAsync(string? requestedPath)
    {
        var generation = ++_loadGeneration;
        _status.ForeColor = Color.FromArgb(71, 85, 105);
        _status.Text = "Klasörler okunuyor...";

        try
        {
            var result = await Task.Run(() => ReadFolders(requestedPath));
            if (IsDisposed || generation != _loadGeneration) return;

            _currentPath = result.CurrentPath;
            _path.Text = result.CurrentPath;
            _folders.BeginUpdate();
            try
            {
                _folders.Items.Clear();
                _folders.Items.AddRange(result.Entries.Cast<object>().ToArray());
            }
            finally
            {
                _folders.EndUpdate();
            }

            _status.Text = result.Truncated
                ? "İlk 1.000 klasör gösteriliyor. Daha dar bir yol yazabilirsiniz."
                : $"{result.Entries.Length} klasör bulundu.";
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            if (IsDisposed || generation != _loadGeneration) return;
            _status.ForeColor = Color.FromArgb(220, 38, 38);
            _status.Text = "Klasörler okunamadı: " + ex.Message;
        }
    }

    private static (string CurrentPath, FolderEntry[] Entries, bool Truncated) ReadFolders(string? requestedPath)
    {
        if (string.IsNullOrWhiteSpace(requestedPath))
        {
            var drives = Directory.GetLogicalDrives()
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Select(path => new FolderEntry(path, path))
                .ToArray();
            return (string.Empty, drives, false);
        }

        var current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(requestedPath.Trim().Trim('"')));
        var paths = Directory.EnumerateDirectories(current).Take(1001).ToArray();
        var truncated = paths.Length > 1000;
        var entries = paths.Take(1000)
            .Select(path => new FolderEntry(Path.GetFileName(path), path))
            .OrderBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        return (current, entries, truncated);
    }

    private void AcceptPath()
    {
        var value = _path.Text.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value))
        {
            _status.ForeColor = Color.FromArgb(220, 38, 38);
            _status.Text = "Sürücü harfiyle veya \\\\sunucu\\paylasim biçiminde tam bir yol seçin.";
            return;
        }

        try
        {
            SelectedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            _status.ForeColor = Color.FromArgb(220, 38, 38);
            _status.Text = "Klasör yolu geçerli değil: " + ex.Message;
        }
    }
}
