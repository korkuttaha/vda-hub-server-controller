using System.Text.Json;
using VdaHubServerController.Models;

namespace VdaHubServerController.Services;

// A slow/unavailable WUA scan must never hold up the five-minute metrics loop.
public sealed class WindowsUpdateMonitorService
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };
    private readonly Func<CancellationToken, Task<WindowsUpdateSnapshot>> _scan;
    private readonly TimeProvider _clock;
    private readonly string _cachePath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private WindowsUpdateSnapshot? _current;

    public WindowsUpdateMonitorService() : this(WindowsUpdateScanner.ScanAsync, TimeProvider.System,
        Path.Combine(ConfigService.InstallDirectory, "windows-update-status.json")) { }

    public WindowsUpdateMonitorService(Func<CancellationToken, Task<WindowsUpdateSnapshot>> scan, TimeProvider clock, string cachePath)
    {
        _scan = scan;
        _clock = clock;
        _cachePath = cachePath;
        try
        {
            if (File.Exists(cachePath) && new FileInfo(cachePath).Length <= 128 * 1024)
            {
                var saved = JsonSerializer.Deserialize<WindowsUpdateSnapshot>(File.ReadAllText(cachePath), Json);
                if (saved is not null && saved.CheckedAtUtc <= _clock.GetUtcNow().UtcDateTime &&
                    saved.CheckedAtUtc > DateTime.UnixEpoch && (saved.Status == "error" || ValidSuccess(saved)))
                    _current = Bound(saved);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A missing/corrupt cache means unknown until a real scan succeeds.
        }
    }

    public WindowsUpdateSnapshot? Current => Volatile.Read(ref _current);

    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await RefreshIfDueAsync(ct);
                await Task.Delay(TimeSpan.FromMinutes(1), _clock, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    public async Task RefreshIfDueAsync(CancellationToken ct)
    {
        if (!await _gate.WaitAsync(0, ct)) return;
        try
        {
            var now = _clock.GetUtcNow().UtcDateTime;
            var previous = Current;
            var interval = previous?.Status == "success" ? TimeSpan.FromHours(6) : TimeSpan.FromHours(1);
            if (previous is not null && now >= previous.CheckedAtUtc && now - previous.CheckedAtUtc < interval) return;

            WindowsUpdateSnapshot result;
            try
            {
                result = await _scan(ct);
                if (!ValidSuccess(result))
                    throw new InvalidDataException("Windows Update did not return a complete successful scan.");
                now = _clock.GetUtcNow().UtcDateTime;
                result = Bound(result with { CheckedAtUtc = now, LastSuccessfulScanAtUtc = now, Error = null });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                result = new WindowsUpdateSnapshot
                {
                    Status = "error", CheckedAtUtc = _clock.GetUtcNow().UtcDateTime,
                    LastSuccessfulScanAtUtc = previous?.LastSuccessfulScanAtUtc,
                    Error = ex is TimeoutException ? "Windows Update taraması zaman aşımına uğradı." :
                        "Windows Update taraması başarısız. Windows Update hizmetini ve güncelleme kaynağına erişimi kontrol edin."
                };
            }
            Volatile.Write(ref _current, result);
            Save(result);
        }
        finally { _gate.Release(); }
    }

    private void Save(WindowsUpdateSnapshot value)
    {
        var temporary = _cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(value, Json));
            File.Move(temporary, _cachePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("Windows Update önbelleği kaydedilemedi; ölçüm raporları devam ediyor.");
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static bool ValidSuccess(WindowsUpdateSnapshot value) => value.Status == "success" &&
        value.PendingCount is >= 0 and <= 100000 && value.SecurityCount is >= 0 &&
        value.SecurityCount <= value.PendingCount && value.RebootRequired.HasValue;

    private static WindowsUpdateSnapshot Bound(WindowsUpdateSnapshot value)
    {
        static string Clip(string? text, int length) => (text ?? "")[..Math.Min(text?.Length ?? 0, length)];
        if (value.Status != "success")
            return value with { Status = "error", PendingCount = null, SecurityCount = null, RebootRequired = null, Updates = [], Error = Clip(value.Error, 300) };
        var updates = (value.Updates ?? []).OfType<WindowsUpdateItem>().Take(100).Select(item => item with
        {
            UpdateId = Clip(item.UpdateId, 128), Title = Clip(item.Title, 300), Severity = Clip(item.Severity, 32),
            KbArticleIds = (item.KbArticleIds ?? []).Take(8).Select(kb => Clip(kb, 24)).ToList()
        }).ToList();
        var result = value with { Updates = updates, UpdatesTruncated = value.UpdatesTruncated || value.Updates?.Count > updates.Count };
        while (updates.Count > 0 && JsonSerializer.SerializeToUtf8Bytes(result, Json).Length > 96 * 1024)
        {
            updates.RemoveAt(updates.Count - 1);
            result = result with { UpdatesTruncated = true };
        }
        return result;
    }
}
