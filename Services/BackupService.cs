using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VdaHubServerController.Models;

namespace VdaHubServerController.Services;

public sealed class ManualBackupRequest
{
    public string RequestId { get; set; } = string.Empty;
    public DateTime RequestedAtUtc { get; set; }
    public string SnapshotFolder { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
}

public sealed class BackupPlanResponse
{
    public bool Configured { get; set; }
    public bool Enabled { get; set; }
    public bool Ready { get; set; }
    public bool ManualOnly { get; set; }
    public string? Message { get; set; }
    public List<string> Paths { get; set; } = [];
    public ManualBackupRequest? ManualRequest { get; set; }
    public string? DestinationRoot { get; set; }
    public int ChunkSizeBytes { get; set; } = 8 * 1024 * 1024;
    public int MassChangeFileThreshold { get; set; } = 500;
    public double MassChangePercentThreshold { get; set; } = 15;
    public int MinimumFilesForPercentThreshold { get; set; } = 100;
    public int RenameLikeThreshold { get; set; } = 100;
    public string? ApprovedAnomalyFingerprint { get; set; }
}

public sealed record BackupExecutionResult(
    bool Ran,
    bool Success,
    string Message,
    int FilesScanned = 0,
    int FilesUploaded = 0,
    long BytesUploaded = 0,
    bool RequiresApproval = false);

public sealed class BackupService
{
    private const string BackupProtocol = "2";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private readonly ConfigService _config;
    private readonly string _statePath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private sealed class State
    {
        public string? LastCompletedRequestId { get; set; }
        public string? LastCompletedSnapshotPath { get; set; }
        public int LastFilesScanned { get; set; }
        public int LastFilesUploaded { get; set; }
        public long LastBytesUploaded { get; set; }
        public List<StateFile> Files { get; set; } = [];
    }

    private sealed class StateFile
    {
        public string Path { get; set; } = string.Empty;
        public long Length { get; set; }
        public long LastWriteUtcTicks { get; set; }
        public bool Deleted { get; set; }
    }

    private sealed record CurrentFile(FileInfo File, string RootLabel, string Relative);
    private sealed record Change(string Path, CurrentFile Item, StateFile? Previous, string Kind);

    public BackupService(ConfigService config)
    {
        _config = config;
        _statePath = Path.Combine(Path.GetDirectoryName(config.ConfigFilePath)!, "backup-state.json");
    }

    public async Task<BackupPlanResponse?> GetPlanAsync(CancellationToken ct = default)
    {
        var settings = _config.Current.Hub;
        if (!TryBridgeBase(settings, out var baseUri) || string.IsNullOrWhiteSpace(settings.ApiKey))
            return null;

        using var request = Authorized(
            HttpMethod.Get,
            new Uri(baseUri, $"/api/server-controller/bridge/backup-plan?serverId={Uri.EscapeDataString(_config.Current.ServerId)}"),
            settings.ApiKey);
        using var response = await Http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) return null;
        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<BackupPlanResponse>(json, JsonOptions());
    }

    public async Task<(bool Success, string Message)> SavePlanAsync(
        bool enabled,
        IReadOnlyList<string> paths,
        CancellationToken ct = default)
    {
        var settings = _config.Current.Hub;
        if (!TryBridgeBase(settings, out var baseUri) || string.IsNullOrWhiteSpace(settings.ApiKey))
            return (false, "Hub bağlantısı yapılandırılmamış.");

        var payload = JsonSerializer.Serialize(new
        {
            serverId = _config.Current.ServerId,
            enabled,
            paths
        }, JsonOptions());

        using var request = Authorized(
            HttpMethod.Post,
            new Uri(baseUri, "/api/server-controller/bridge/backup-plan"),
            settings.ApiKey);
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await Http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        return response.IsSuccessStatusCode
            ? (true, "Manuel yedekleme klasörleri Hub'a kaydedildi.")
            : (false, $"Hub yedekleme ayarı reddedildi (HTTP {(int)response.StatusCode}): {body}");
    }

    public Task<BackupExecutionResult> RunIfDueAsync(bool force = false, CancellationToken ct = default) =>
        RunPendingAsync(ct);

    public async Task<BackupExecutionResult> RunPendingAsync(CancellationToken ct = default)
    {
        if (!await _gate.WaitAsync(0, ct))
            return new(false, true, "Yedekleme zaten çalışıyor.");

        try
        {
            var plan = await GetPlanAsync(ct);
            if (plan is null) return new(false, false, "Hub yedekleme planı alınamadı.");
            var command = plan.ManualRequest;
            if (command is null) return new(false, true, "Bekleyen manuel yedekleme komutu yok.");
            if (string.IsNullOrWhiteSpace(command.RequestId) || string.IsNullOrWhiteSpace(command.SnapshotFolder))
                return new(false, false, "Hub geçersiz yedekleme komutu döndürdü.");

            var state = LoadState();
            if (state.LastCompletedRequestId == command.RequestId && !string.IsNullOrWhiteSpace(state.LastCompletedSnapshotPath))
            {
                await ReportAsync(command.RequestId, true, state.LastFilesScanned, state.LastFilesUploaded,
                    state.LastBytesUploaded, state.LastCompletedSnapshotPath, null, ct: ct);
                return new(false, true, "Tamamlanan snapshot sonucu Hub'a yeniden bildirildi.",
                    state.LastFilesScanned, state.LastFilesUploaded, state.LastBytesUploaded);
            }

            if (!await StartRequestAsync(command.RequestId, ct))
                return new(false, false, "Hub yedekleme komutu artık geçerli değil.");

            if (!plan.Enabled)
                return await FailAsync(command.RequestId, "Yedekleme klasörleri etkin değil.", ct);
            if (!plan.Ready)
                return await FailAsync(command.RequestId, plan.Message ?? "Yedekleme planı hazır değil.", ct);
            if (string.IsNullOrWhiteSpace(plan.DestinationRoot))
                return await FailAsync(command.RequestId, "Dropbox hedefi eksik.", ct);

            var manifest = state.Files
                .Where(x => !string.IsNullOrWhiteSpace(x.Path))
                .GroupBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.Last(), StringComparer.OrdinalIgnoreCase);
            var scan = Scan(plan);
            if (scan.Errors.Count > 0)
            {
                var error = string.Join(" | ", scan.Errors.Take(8));
                await ReportAsync(command.RequestId, false, scan.Files.Count, 0, 0, null, error, ct: ct);
                return new(true, false, error, scan.Files.Count);
            }

            var changes = new List<Change>();
            var descriptors = new List<string>();
            var missing = new Dictionary<string, StateFile>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in manifest)
            {
                if (!scan.Files.ContainsKey(pair.Key) && !pair.Value.Deleted)
                {
                    missing[pair.Key] = pair.Value;
                    descriptors.Add("D|" + pair.Key);
                }
            }
            foreach (var pair in scan.Files)
            {
                manifest.TryGetValue(pair.Key, out var previous);
                var kind = "N";
                var changed = previous is null;
                if (previous is not null &&
                    (previous.Deleted || previous.Length != pair.Value.File.Length ||
                     previous.LastWriteUtcTicks != pair.Value.File.LastWriteTimeUtc.Ticks))
                {
                    kind = "M";
                    changed = true;
                }
                if (!changed) continue;
                changes.Add(new(pair.Key, pair.Value, previous, kind));
                descriptors.Add($"{kind}|{pair.Key}|{pair.Value.File.Length}|{pair.Value.File.LastWriteTimeUtc.Ticks}");
            }

            var renameLike = 0;
            foreach (var change in changes.Where(x => x.Kind == "N"))
            {
                var previousName = Path.Combine(
                    Path.GetDirectoryName(change.Path) ?? string.Empty,
                    Path.GetFileNameWithoutExtension(change.Path));
                if (missing.ContainsKey(previousName)) renameLike++;
            }

            var changedFiles = changes.Count + missing.Count;
            var baselineCount = manifest.Values.Count(x => !x.Deleted);
            var denominator = Math.Max(1, Math.Max(baselineCount, scan.Files.Count));
            var changedPercent = Math.Round(100d * changedFiles / denominator, 2);
            var fingerprint = changedFiles > 0 ? Fingerprint(descriptors) : string.Empty;
            var massChange = manifest.Count > 0 &&
                (changedFiles >= plan.MassChangeFileThreshold ||
                 (denominator >= plan.MinimumFilesForPercentThreshold &&
                  changedPercent >= plan.MassChangePercentThreshold) ||
                 renameLike >= plan.RenameLikeThreshold);
            var approved = !string.IsNullOrWhiteSpace(fingerprint) &&
                string.Equals(plan.ApprovedAnomalyFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase);
            if (massChange && !approved)
            {
                var message = $"Olası ransomware/toplu değişiklik: {changedFiles} dosya (%{changedPercent:0.##}), şüpheli yeniden adlandırma: {renameLike}. Yeni snapshot oluşturulmadı.";
                await ReportAsync(command.RequestId, false, scan.Files.Count, 0, 0, null, message,
                    true, fingerprint, changedFiles, changedPercent, renameLike, ct);
                return new(true, false, message, scan.Files.Count, RequiresApproval: true);
            }

            var accessToken = await GetAccessTokenAsync(command.RequestId, ct);
            if (string.IsNullOrWhiteSpace(accessToken))
                return await FailAsync(command.RequestId, "Dropbox yükleme yetkisi alınamadı.", ct, scan.Files.Count);

            var stagingRoot = $"{plan.DestinationRoot!.TrimEnd('/')}/.uploading/{command.RequestId}";
            var finalRoot = $"{plan.DestinationRoot.TrimEnd('/')}/{command.SnapshotFolder}";
            var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var errors = new List<string>();
            var filesUploaded = 0;
            long bytesUploaded = 0;
            var chunkSize = Math.Clamp(plan.ChunkSizeBytes, 1024 * 1024, 64 * 1024 * 1024);
            await EnsureFolderPathAsync(stagingRoot, accessToken, folders, ct);

            foreach (var item in scan.Files.Values.OrderBy(x => x.File.FullName, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var remotePath = $"{stagingRoot}/{item.RootLabel}/{item.Relative}";
                    await EnsureFolderPathAsync(remotePath[..remotePath.LastIndexOf('/')], accessToken, folders, ct);
                    await UploadAsync(item.File, remotePath, accessToken, chunkSize, ct);
                    filesUploaded++;
                    bytesUploaded += item.File.Length;
                }
                catch (Exception ex)
                {
                    errors.Add(ex.Message);
                }
            }

            if (errors.Count > 0)
            {
                var errorText = string.Join(" | ", errors.Take(8));
                await ReportAsync(command.RequestId, false, scan.Files.Count, filesUploaded, bytesUploaded, null,
                    errorText, false, null, changedFiles, changedPercent, renameLike, ct);
                return new(true, false, errorText, scan.Files.Count, filesUploaded, bytesUploaded);
            }

            try
            {
                await MoveSnapshotAsync(stagingRoot, finalRoot, accessToken, ct);
            }
            catch (Exception ex)
            {
                await ReportAsync(command.RequestId, false, scan.Files.Count, filesUploaded, bytesUploaded,
                    null, ex.Message, false, null, changedFiles, changedPercent, renameLike, ct);
                return new(true, false, ex.Message, scan.Files.Count, filesUploaded, bytesUploaded);
            }
            state.Files = scan.Files.Select(pair => new StateFile
            {
                Path = pair.Key,
                Length = pair.Value.File.Length,
                LastWriteUtcTicks = pair.Value.File.LastWriteTimeUtc.Ticks,
                Deleted = false
            }).OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase).ToList();
            state.LastCompletedRequestId = command.RequestId;
            state.LastCompletedSnapshotPath = finalRoot;
            state.LastFilesScanned = scan.Files.Count;
            state.LastFilesUploaded = filesUploaded;
            state.LastBytesUploaded = bytesUploaded;
            SaveState(state);

            await ReportAsync(command.RequestId, true, scan.Files.Count, filesUploaded, bytesUploaded,
                finalRoot, null, false, null, changedFiles, changedPercent, renameLike, ct);
            return new(true, true,
                $"Tarih damgalı snapshot tamamlandı: {filesUploaded}/{scan.Files.Count} dosya, {FormatBytes(bytesUploaded)}.",
                scan.Files.Count, filesUploaded, bytesUploaded);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<BackupExecutionResult> FailAsync(
        string requestId,
        string message,
        CancellationToken ct,
        int filesScanned = 0)
    {
        await ReportAsync(requestId, false, filesScanned, 0, 0, null, message, ct: ct);
        return new(true, false, message, filesScanned);
    }

    private (Dictionary<string, CurrentFile> Files, List<string> Errors) Scan(BackupPlanResponse plan)
    {
        var files = new Dictionary<string, CurrentFile>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();
        foreach (var rawRoot in plan.Paths)
        {
            try
            {
                var root = ValidateRoot(rawRoot);
                var rootLabel = RootLabel(root);
                foreach (var file in EnumerateFilesSafe(root))
                {
                    var relative = Path.GetRelativePath(root, file.FullName).Replace('\\', '/');
                    files[file.FullName] = new(file, rootLabel, relative);
                }
            }
            catch (Exception ex)
            {
                errors.Add(ex.Message);
            }
        }
        return (files, errors);
    }

    private static IEnumerable<FileInfo> EnumerateFilesSafe(string root)
    {
        var stack = new Stack<DirectoryInfo>();
        stack.Push(new DirectoryInfo(root));
        while (stack.Count > 0)
        {
            var directory = stack.Pop();
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            FileInfo[] files;
            DirectoryInfo[] directories;
            try
            {
                files = directory.GetFiles();
                directories = directory.GetDirectories();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new IOException($"Klasör okunamadı: {directory.FullName}", ex);
            }
            foreach (var file in files)
                if ((file.Attributes & FileAttributes.ReparsePoint) == 0) yield return file;
            foreach (var child in directories)
                if ((child.Attributes & FileAttributes.ReparsePoint) == 0) stack.Push(child);
        }
    }

    private static string ValidateRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) ||
            path.StartsWith(@"\\", StringComparison.Ordinal) || path.Contains("..", StringComparison.Ordinal))
            throw new ArgumentException($"Geçersiz yerel Windows klasör yolu: {path}");
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException($"Yedek klasörü bulunamadı: {full}");
        if ((new DirectoryInfo(full).Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"Junction/symlink yedek kökü desteklenmiyor: {full}");
        return full;
    }

    private static string RootLabel(string root)
    {
        var drive = Path.GetPathRoot(root)?.TrimEnd('\\', '/').TrimEnd(':') ?? "X";
        var leaf = Path.GetFileName(root.TrimEnd('\\')) is { Length: > 0 } name ? name : "root";
        var safe = new string(leaf.Select(ch => char.IsLetterOrDigit(ch) || ch is '.' or '_' or '-' ? ch : '_').ToArray()).Trim('_');
        if (string.IsNullOrWhiteSpace(safe)) safe = "folder";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.ToUpperInvariant()))).ToLowerInvariant()[..12];
        return $"{drive}-{safe}-{hash}";
    }

    private async Task<bool> StartRequestAsync(string requestId, CancellationToken ct)
    {
        var settings = _config.Current.Hub;
        if (!TryBridgeBase(settings, out var baseUri)) return false;
        var payload = JsonSerializer.Serialize(new { serverId = _config.Current.ServerId, requestId }, JsonOptions());
        using var request = Authorized(HttpMethod.Post, new Uri(baseUri, "/api/server-controller/bridge/backup-start"), settings.ApiKey);
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await Http.SendAsync(request, ct);
        return response.IsSuccessStatusCode;
    }

    private async Task<string?> GetAccessTokenAsync(string requestId, CancellationToken ct)
    {
        var settings = _config.Current.Hub;
        if (!TryBridgeBase(settings, out var baseUri)) return null;
        using var request = Authorized(
            HttpMethod.Get,
            new Uri(baseUri, $"/api/server-controller/bridge/backup-token?serverId={Uri.EscapeDataString(_config.Current.ServerId)}&requestId={Uri.EscapeDataString(requestId)}"),
            settings.ApiKey);
        using var response = await Http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) return null;
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return document.RootElement.TryGetProperty("accessToken", out var token) ? token.GetString() : null;
    }

    private async Task ReportAsync(
        string requestId,
        bool success,
        int filesScanned,
        int filesUploaded,
        long bytesUploaded,
        string? snapshotPath,
        string? error,
        bool requiresApproval = false,
        string? fingerprint = null,
        int changedFiles = 0,
        double changedPercent = 0,
        int renameLikeChanges = 0,
        CancellationToken ct = default)
    {
        var settings = _config.Current.Hub;
        if (!TryBridgeBase(settings, out var baseUri)) return;
        var payload = JsonSerializer.Serialize(new
        {
            serverId = _config.Current.ServerId,
            requestId,
            success,
            filesScanned,
            filesUploaded,
            bytesUploaded,
            snapshotPath,
            error,
            requiresApproval,
            anomalyFingerprint = fingerprint,
            changedFiles,
            changedPercent,
            renameLikeChanges
        }, JsonOptions());
        using var request = Authorized(HttpMethod.Post, new Uri(baseUri, "/api/server-controller/bridge/backup-result"), settings.ApiKey);
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        try { using var _ = await Http.SendAsync(request, ct); } catch { }
    }

    private static async Task EnsureFolderPathAsync(
        string folderPath,
        string accessToken,
        HashSet<string> cache,
        CancellationToken ct)
    {
        var current = string.Empty;
        foreach (var segment in folderPath.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current += "/" + segment;
            if (!cache.Add(current)) continue;
            using var response = await DropboxJsonAsync(
                "https://api.dropboxapi.com/2/files/create_folder_v2",
                accessToken,
                new { path = current, autorename = false },
                ct);
            if (response.StatusCode != HttpStatusCode.Conflict && !response.IsSuccessStatusCode)
                throw new IOException($"Dropbox klasörü hazırlanamadı (HTTP {(int)response.StatusCode}): {current}");
        }
    }

    private static async Task MoveSnapshotAsync(
        string source,
        string destination,
        string accessToken,
        CancellationToken ct)
    {
        using var response = await DropboxJsonAsync(
            "https://api.dropboxapi.com/2/files/move_v2",
            accessToken,
            new { from_path = source, to_path = destination, autorename = false, allow_ownership_transfer = false },
            ct);
        if (!response.IsSuccessStatusCode)
            throw new IOException($"Tamamlanan snapshot tarih klasörüne taşınamadı: {destination}");
    }

    private static async Task UploadAsync(
        FileInfo file,
        string remotePath,
        string accessToken,
        int chunkSize,
        CancellationToken ct)
    {
        const long maxFile = 2_199_019_061_248L;
        if (file.Length > maxFile) throw new IOException($"Dosya Dropbox sınırını aşıyor: {file.FullName}");
        var initialLength = file.Length;
        var initialTicks = file.LastWriteTimeUtc.Ticks;
        await using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, chunkSize, true);
        var buffer = new byte[chunkSize];
        var first = await ReadChunkAsync(stream, buffer, ct);
        using var start = await DropboxContentAsync(
            "https://content.dropboxapi.com/2/files/upload_session/start",
            accessToken,
            new { close = false },
            buffer,
            first,
            ct);
        if (!start.IsSuccessStatusCode) throw new IOException($"Dropbox oturumu başlatılamadı: {file.FullName}");
        using var startDoc = JsonDocument.Parse(await start.Content.ReadAsStringAsync(ct));
        var sessionId = startDoc.RootElement.GetProperty("session_id").GetString()
            ?? throw new IOException("Dropbox session id eksik.");
        long offset = first;

        while (offset < initialLength)
        {
            var count = await ReadChunkAsync(stream, buffer, ct);
            if (count <= 0) throw new EndOfStreamException(file.FullName);
            var final = offset + count >= initialLength;
            var arg = final
                ? (object)new
                {
                    cursor = new { session_id = sessionId, offset },
                    commit = new { path = remotePath, mode = "overwrite", autorename = false, mute = true, strict_conflict = false }
                }
                : new { cursor = new { session_id = sessionId, offset }, close = false };
            var url = final
                ? "https://content.dropboxapi.com/2/files/upload_session/finish"
                : "https://content.dropboxapi.com/2/files/upload_session/append_v2";
            using var response = await DropboxContentAsync(url, accessToken, arg, buffer, count, ct);
            if (!response.IsSuccessStatusCode) throw new IOException($"Dropbox yüklemesi başarısız: {file.FullName}");
            offset += count;
        }

        if (initialLength <= first)
        {
            var arg = new
            {
                cursor = new { session_id = sessionId, offset },
                commit = new { path = remotePath, mode = "overwrite", autorename = false, mute = true, strict_conflict = false }
            };
            using var response = await DropboxContentAsync(
                "https://content.dropboxapi.com/2/files/upload_session/finish",
                accessToken,
                arg,
                [],
                0,
                ct);
            if (!response.IsSuccessStatusCode) throw new IOException($"Dropbox yüklemesi tamamlanamadı: {file.FullName}");
        }

        file.Refresh();
        if (file.Length != initialLength || file.LastWriteTimeUtc.Ticks != initialTicks)
            throw new IOException($"Dosya yedeklenirken değişti; snapshot tamamlanmadı: {file.FullName}");
    }

    private static async Task<int> ReadChunkAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct);
            if (read == 0) break;
            total += read;
        }
        return total;
    }

    private static async Task<HttpResponseMessage> DropboxJsonAsync(
        string url,
        string accessToken,
        object payload,
        CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return await Http.SendAsync(request, ct);
    }

    private static async Task<HttpResponseMessage> DropboxContentAsync(
        string url,
        string accessToken,
        object arg,
        byte[] buffer,
        int count,
        CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation("Dropbox-API-Arg", JsonSerializer.Serialize(arg));
        request.Content = new ByteArrayContent(buffer, 0, count);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    private State LoadState()
    {
        if (!File.Exists(_statePath)) return new();
        try
        {
            var state = JsonSerializer.Deserialize<State>(File.ReadAllText(_statePath), JsonOptions())
                ?? throw new InvalidDataException("Yerel yedekleme durumu boş.");
            state.Files ??= [];
            return state;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new InvalidDataException(
                "Yerel yedekleme durumu okunamadı; ransomware baseline'ını atlamamak için işlem durduruldu.", ex);
        }
    }

    private void SaveState(State state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
        var temporary = _statePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, _statePath, true);
    }

    private static string Fingerprint(IEnumerable<string> descriptors)
    {
        using var sha = SHA256.Create();
        foreach (var descriptor in descriptors.OrderBy(x => x, StringComparer.Ordinal))
        {
            var bytes = Encoding.UTF8.GetBytes(descriptor + Environment.NewLine);
            sha.TransformBlock(bytes, 0, bytes.Length, bytes, 0);
        }
        sha.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }

    private static bool TryBridgeBase(HubSettings settings, out Uri baseUri)
    {
        baseUri = null!;
        if (!Uri.TryCreate(settings.HubApiUrl, UriKind.Absolute, out var report) ||
            report.Scheme != Uri.UriSchemeHttps) return false;
        baseUri = new Uri(report.GetLeftPart(UriPartial.Authority));
        return true;
    }

    private static HttpRequestMessage Authorized(HttpMethod method, Uri uri, string apiKey)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Add("X-API-Key", apiKey);
        request.Headers.Add("X-VDA-Backup-Protocol", BackupProtocol);
        return request;
    }

    private static JsonSerializerOptions JsonOptions() => new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static string FormatBytes(long value) =>
        value >= 1024L * 1024 * 1024 ? $"{value / 1024d / 1024 / 1024:0.00} GB" :
        value >= 1024L * 1024 ? $"{value / 1024d / 1024:0.00} MB" :
        $"{value / 1024d:0.00} KB";
}
