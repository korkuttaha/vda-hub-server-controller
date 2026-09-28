using System.Net;
using System.Net.Http.Headers;
using System.IO.Compression;
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
    public bool CancelRequested { get; set; }
}

public sealed class BackupPlanResponse
{
    public bool Configured { get; set; }
    public bool Enabled { get; set; }
    public bool Ready { get; set; }
    public bool ManualOnly { get; set; }
    public string? Message { get; set; }
    public List<string> Paths { get; set; } = [];
    public bool ArchiveEnabled { get; set; } = true;
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
    bool RequiresApproval = false,
    int SkippedFiles = 0);

public sealed class BackupService
{
    private const string BackupProtocol = "3";
    private const int DropboxUploadMaxAttempts = 6;
    private const long ArchivePartTargetBytes = 8L * 1024 * 1024 * 1024;
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
        public List<string> LastSkippedFiles { get; set; } = [];
        public List<StateFile> Files { get; set; } = [];
    }

    private sealed class StateFile
    {
        public string Path { get; set; } = string.Empty;
        public long Length { get; set; }
        public long LastWriteUtcTicks { get; set; }
        public bool Deleted { get; set; }
    }

    private sealed record CurrentFile(
        FileInfo File,
        string Root,
        string RootLabel,
        string Relative,
        long ScannedLength,
        long ScannedLastWriteUtcTicks);
    private sealed record Change(string Path, CurrentFile Item, StateFile? Previous, string Kind);
    private sealed record ArchiveArtifact(
        FileInfo File,
        IReadOnlyList<CurrentFile> SourceFiles,
        string? RootLabel = null,
        string? SourcePath = null,
        int PartNumber = 1,
        int PartCount = 1);
    private sealed record DropboxErrorInfo(string Summary, long? CorrectOffset);
    private sealed record SkippedArchiveFile(CurrentFile Source, string Reason);
    private sealed record ArchiveBuildResult(
        IReadOnlyList<ArchiveArtifact> Artifacts,
        IReadOnlyList<SkippedArchiveFile> SkippedFiles);
    private sealed class BackupStopRequestedException : OperationCanceledException { }
    private sealed class UnstableArchiveFileException(CurrentFile source, string reason) : IOException(reason)
    {
        public CurrentFile FileItem { get; } = source;
    }

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
        bool archiveEnabled,
        CancellationToken ct = default)
    {
        var settings = _config.Current.Hub;
        if (!TryBridgeBase(settings, out var baseUri) || string.IsNullOrWhiteSpace(settings.ApiKey))
            return (false, "Hub bağlantısı yapılandırılmamış.");

        var payload = JsonSerializer.Serialize(new
        {
            serverId = _config.Current.ServerId,
            enabled,
            paths,
            archiveEnabled
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

        string? cleanupToken = null;
        string? cleanupStagingRoot = null;
        string? temporaryRoot = null;
        string? activeRequestId = null;
        var filesScannedForResult = 0;
        var filesUploadedForResult = 0;
        long bytesUploadedForResult = 0;
        try
        {
            var plan = await GetPlanAsync(ct);
            if (plan is null) return new(false, false, "Hub yedekleme planı alınamadı.");
            var command = plan.ManualRequest;
            if (command is null) return new(false, true, "Bekleyen manuel yedekleme komutu yok.");
            activeRequestId = command.RequestId;
            if (string.IsNullOrWhiteSpace(command.RequestId) || string.IsNullOrWhiteSpace(command.SnapshotFolder))
                return new(false, false, "Hub geçersiz yedekleme komutu döndürdü.");

            var state = LoadState();
            if (state.LastCompletedRequestId == command.RequestId && !string.IsNullOrWhiteSpace(state.LastCompletedSnapshotPath))
            {
                var replayWarning = BuildSkippedFilesWarning(state.LastSkippedFiles);
                await ReportAsync(command.RequestId, true, state.LastFilesScanned, state.LastFilesUploaded,
                    state.LastBytesUploaded, state.LastCompletedSnapshotPath, null,
                    skippedFilesCount: state.LastSkippedFiles.Count,
                    skippedFiles: state.LastSkippedFiles,
                    warning: replayWarning,
                    ct: ct);
                return new(false, true, "Tamamlanan snapshot sonucu Hub'a yeniden bildirildi.",
                    state.LastFilesScanned, state.LastFilesUploaded, state.LastBytesUploaded,
                    SkippedFiles: state.LastSkippedFiles.Count);
            }

            if (!await StartRequestAsync(command.RequestId, ct))
                return new(false, false, "Hub yedekleme komutu artık geçerli değil.");

            if (command.CancelRequested ||
                await ReportProgressAsync(command.RequestId, "scanning", 0, 0, 0, 0, ct))
                throw new BackupStopRequestedException();

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
            filesScannedForResult = scan.Files.Count;
            if (scan.Errors.Count > 0)
            {
                var error = string.Join(" | ", scan.Errors.Take(8));
                await ReportAsync(command.RequestId, false, scan.Files.Count, 0, 0, null, error, ct: ct);
                return new(true, false, error, scan.Files.Count);
            }

            var totalBytes = scan.Files.Values.Sum(x => x.ScannedLength);
            if (await ReportProgressAsync(command.RequestId, "preparing", scan.Files.Count, 0, 0, totalBytes, ct))
                throw new BackupStopRequestedException();

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
                    (previous.Deleted || previous.Length != pair.Value.ScannedLength ||
                     previous.LastWriteUtcTicks != pair.Value.ScannedLastWriteUtcTicks))
                {
                    kind = "M";
                    changed = true;
                }
                if (!changed) continue;
                changes.Add(new(pair.Key, pair.Value, previous, kind));
                descriptors.Add($"{kind}|{pair.Key}|{pair.Value.ScannedLength}|{pair.Value.ScannedLastWriteUtcTicks}");
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
                    true, fingerprint, changedFiles, changedPercent, renameLike, ct: ct);
                return new(true, false, message, scan.Files.Count, RequiresApproval: true);
            }

            var accessToken = await GetAccessTokenAsync(command.RequestId, ct);
            if (string.IsNullOrWhiteSpace(accessToken))
                return await FailAsync(command.RequestId, "Dropbox yükleme yetkisi alınamadı.", ct, scan.Files.Count);

            var stagingRoot = $"{plan.DestinationRoot!.TrimEnd('/')}/.uploading/{command.RequestId}";
            var finalRoot = $"{plan.DestinationRoot.TrimEnd('/')}/{command.SnapshotFolder}";
            cleanupToken = accessToken;
            cleanupStagingRoot = stagingRoot;
            var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var errors = new List<string>();
            var filesUploaded = 0;
            long bytesUploaded = 0;
            IReadOnlyList<CurrentFile> snapshotFiles = scan.Files.Values.ToArray();
            IReadOnlyList<SkippedArchiveFile> skippedArchiveFiles = [];
            var lastProgressAtUtc = DateTime.MinValue;
            var chunkSize = Math.Clamp(plan.ChunkSizeBytes, 1024 * 1024, 64 * 1024 * 1024);
            await EnsureFolderPathAsync(stagingRoot, accessToken, folders, ct);

            if (plan.ArchiveEnabled)
            {
                temporaryRoot = CreateTemporaryRoot(
                    command.RequestId,
                    totalBytes,
                    scan.Files.Values.Select(item => item.Root));
                var lastCompressionProgressAtUtc = DateTime.MinValue;
                async Task PushCompressionProgressAsync(int filesCompressed, long bytesCompressed, bool force)
                {
                    var now = DateTime.UtcNow;
                    if (!force && now - lastCompressionProgressAtUtc < TimeSpan.FromSeconds(1)) return;
                    lastCompressionProgressAtUtc = now;
                    if (await ReportProgressAsync(
                            command.RequestId,
                            "compressing",
                            scan.Files.Count,
                            filesCompressed,
                            bytesCompressed,
                            totalBytes,
                            ct))
                        throw new BackupStopRequestedException();
                }

                if (await ReportProgressAsync(
                        command.RequestId,
                        "compressing",
                        scan.Files.Count,
                        0,
                        0,
                        totalBytes,
                        ct))
                    throw new BackupStopRequestedException();
                var archiveBuild = await CreateArchivesAsync(
                    scan.Files.Values,
                    temporaryRoot,
                    command,
                    PushCompressionProgressAsync,
                    ct);
                var artifacts = archiveBuild.Artifacts;
                skippedArchiveFiles = archiveBuild.SkippedFiles;
                var skippedPaths = skippedArchiveFiles
                    .Select(x => x.Source.File.FullName)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                snapshotFiles = scan.Files.Values
                    .Where(x => !skippedPaths.Contains(x.File.FullName))
                    .ToArray();
                await PushCompressionProgressAsync(scan.Files.Count, totalBytes, true);

                var archiveTotalBytes = artifacts.Sum(x => x.File.Length);
                if (await ReportProgressAsync(
                        command.RequestId,
                        "uploading",
                        scan.Files.Count,
                        0,
                        0,
                        archiveTotalBytes,
                        ct))
                    throw new BackupStopRequestedException();

                async Task PushArchiveProgressAsync(long currentArchiveBytes, bool force = false)
                {
                    var now = DateTime.UtcNow;
                    if (!force && now - lastProgressAtUtc < TimeSpan.FromSeconds(1)) return;
                    lastProgressAtUtc = now;
                    if (await ReportProgressAsync(
                            command.RequestId,
                            "uploading",
                            scan.Files.Count,
                            filesUploaded,
                            bytesUploaded + currentArchiveBytes,
                            archiveTotalBytes,
                            ct))
                        throw new BackupStopRequestedException();
                }

                foreach (var artifact in artifacts)
                {
                    try
                    {
                        await UploadAsync(
                            artifact.File,
                            $"{stagingRoot}/{artifact.File.Name}",
                            accessToken,
                            chunkSize,
                            currentArchiveBytes => PushArchiveProgressAsync(currentArchiveBytes),
                            ct);
                        filesUploaded += artifact.SourceFiles.Count;
                        bytesUploaded += artifact.File.Length;
                        filesUploadedForResult = filesUploaded;
                        bytesUploadedForResult = bytesUploaded;
                        await PushArchiveProgressAsync(0);
                    }
                    catch (BackupStopRequestedException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        errors.Add(ex.Message);
                    }
                }
                await PushArchiveProgressAsync(0, true);
            }
            else
            {
                if (await ReportProgressAsync(command.RequestId, "uploading", scan.Files.Count, 0, 0, totalBytes, ct))
                    throw new BackupStopRequestedException();

                async Task PushProgressAsync(long currentFileBytes, bool force = false)
                {
                    var now = DateTime.UtcNow;
                    if (!force && now - lastProgressAtUtc < TimeSpan.FromSeconds(1)) return;
                    lastProgressAtUtc = now;
                    if (await ReportProgressAsync(
                            command.RequestId,
                            "uploading",
                            scan.Files.Count,
                            filesUploaded,
                            bytesUploaded + currentFileBytes,
                            totalBytes,
                            ct))
                        throw new BackupStopRequestedException();
                }

                foreach (var item in scan.Files.Values.OrderBy(x => x.File.FullName, StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        var remotePath = $"{stagingRoot}/{item.RootLabel}/{item.Relative}";
                        await EnsureFolderPathAsync(remotePath[..remotePath.LastIndexOf('/')], accessToken, folders, ct);
                        await UploadAsync(
                            item.File,
                            remotePath,
                            accessToken,
                            chunkSize,
                            currentFileBytes => PushProgressAsync(currentFileBytes),
                            ct);
                        filesUploaded++;
                        bytesUploaded += item.File.Length;
                        filesUploadedForResult = filesUploaded;
                        bytesUploadedForResult = bytesUploaded;
                        await PushProgressAsync(0);
                    }
                    catch (BackupStopRequestedException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        errors.Add(ex.Message);
                    }
                }
                await PushProgressAsync(0, true);
            }

            if (errors.Count > 0)
            {
                await DeleteDropboxPathIfExistsAsync(stagingRoot, accessToken, ct);
                var errorText = string.Join(" | ", errors.Take(8));
                await ReportAsync(command.RequestId, false, scan.Files.Count, filesUploaded, bytesUploaded, null,
                    errorText, false, null, changedFiles, changedPercent, renameLike, ct: ct);
                return new(true, false, errorText, scan.Files.Count, filesUploaded, bytesUploaded);
            }

            try
            {
                if (await ReportProgressAsync(
                        command.RequestId,
                        "finalizing",
                        scan.Files.Count,
                        filesUploaded,
                        bytesUploaded,
                        bytesUploaded,
                        ct))
                    throw new BackupStopRequestedException();
                await MoveSnapshotAsync(stagingRoot, finalRoot, accessToken, ct);
                cleanupStagingRoot = null;
            }
            catch (BackupStopRequestedException)
            {
                throw;
            }
            catch (Exception ex)
            {
                await DeleteDropboxPathIfExistsAsync(stagingRoot, accessToken, CancellationToken.None);
                await ReportAsync(command.RequestId, false, scan.Files.Count, filesUploaded, bytesUploaded,
                    null, ex.Message, false, null, changedFiles, changedPercent, renameLike, ct: ct);
                return new(true, false, ex.Message, scan.Files.Count, filesUploaded, bytesUploaded);
            }
            state.Files = snapshotFiles.Select(item => new StateFile
            {
                Path = item.File.FullName,
                Length = item.ScannedLength,
                LastWriteUtcTicks = item.ScannedLastWriteUtcTicks,
                Deleted = false
            }).OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase).ToList();
            state.LastCompletedRequestId = command.RequestId;
            state.LastCompletedSnapshotPath = finalRoot;
            state.LastFilesScanned = scan.Files.Count;
            state.LastFilesUploaded = filesUploaded;
            state.LastBytesUploaded = bytesUploaded;
            state.LastSkippedFiles = skippedArchiveFiles.Select(x => x.Source.File.FullName).ToList();
            SaveState(state);

            var warning = BuildSkippedFilesWarning(state.LastSkippedFiles);
            await ReportAsync(command.RequestId, true, scan.Files.Count, filesUploaded, bytesUploaded,
                finalRoot, null, false, null, changedFiles, changedPercent, renameLike,
                skippedFilesCount: state.LastSkippedFiles.Count,
                skippedFiles: state.LastSkippedFiles,
                warning: warning,
                ct: ct);
            var skippedSuffix = state.LastSkippedFiles.Count > 0
                ? $" {state.LastSkippedFiles.Count} değişken dosya atlandı."
                : string.Empty;
            return new(true, true,
                $"Tarih damgalı snapshot tamamlandı: {filesUploaded}/{scan.Files.Count} dosya, {FormatBytes(bytesUploaded)}.{skippedSuffix}",
                scan.Files.Count, filesUploaded, bytesUploaded,
                SkippedFiles: state.LastSkippedFiles.Count);
        }
        catch (BackupStopRequestedException)
        {
            if (!string.IsNullOrWhiteSpace(cleanupToken) && !string.IsNullOrWhiteSpace(cleanupStagingRoot))
                await DeleteDropboxPathIfExistsAsync(cleanupStagingRoot, cleanupToken, CancellationToken.None);
            if (!string.IsNullOrWhiteSpace(activeRequestId))
                await ReportAsync(
                    activeRequestId,
                    false,
                    filesScannedForResult,
                    filesUploadedForResult,
                    bytesUploadedForResult,
                    null,
                    "Yedekleme kullanıcı tarafından durduruldu.",
                    cancelled: true,
                    ct: CancellationToken.None);
            return new(
                true,
                false,
                "Yedekleme kullanıcı tarafından durduruldu; geçici dosyalar temizlendi.",
                filesScannedForResult,
                filesUploadedForResult,
                bytesUploadedForResult);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (!string.IsNullOrWhiteSpace(cleanupToken) && !string.IsNullOrWhiteSpace(cleanupStagingRoot))
                await DeleteDropboxPathIfExistsAsync(cleanupStagingRoot, cleanupToken, CancellationToken.None);
            if (!string.IsNullOrWhiteSpace(activeRequestId))
                await ReportAsync(
                    activeRequestId,
                    false,
                    filesScannedForResult,
                    filesUploadedForResult,
                    bytesUploadedForResult,
                    null,
                    ex.Message,
                    ct: CancellationToken.None);
            return new(
                true,
                false,
                ex.Message,
                filesScannedForResult,
                filesUploadedForResult,
                bytesUploadedForResult);
        }
        finally
        {
            DeleteTemporaryRoot(temporaryRoot);
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

    private static string CreateTemporaryRoot(
        string requestId,
        long sourceBytes,
        IEnumerable<string> sourceRoots)
    {
        var safety = Math.Max(512L * 1024 * 1024, sourceBytes / 20);
        var required = sourceBytes > long.MaxValue - safety ? long.MaxValue : sourceBytes + safety;
        var candidates = new List<string>
        {
            Path.Combine(Path.GetTempPath(), "VDAKor", "backups")
        };
        foreach (var sourceRoot in sourceRoots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var volumeRoot = Path.GetPathRoot(Path.GetFullPath(sourceRoot));
            if (!string.IsNullOrWhiteSpace(volumeRoot))
                candidates.Add(Path.Combine(volumeRoot, "VDAKor", "backups"));
        }

        var inspected = new List<string>();
        string? basePath = null;
        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var volumeRoot = Path.GetPathRoot(Path.GetFullPath(candidate));
                if (string.IsNullOrWhiteSpace(volumeRoot)) continue;
                var available = new DriveInfo(volumeRoot).AvailableFreeSpace;
                inspected.Add($"{volumeRoot} {FormatBytes(available)}");
                if (available < required) continue;
                Directory.CreateDirectory(candidate);
                basePath = candidate;
                break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                inspected.Add($"{candidate} kullanılamıyor");
            }
        }

        if (basePath is null)
            throw new IOException(
                $"ZIP hazırlamak için uygun geçici diskte yeterli boş alan yok. Gerekli: {FormatBytes(required)}. Kontrol edilenler: {string.Join("; ", inspected)}.");

        foreach (var stale in new DirectoryInfo(basePath).EnumerateDirectories())
        {
            if (stale.Name.Length != 32 || !stale.Name.All(Uri.IsHexDigit) ||
                stale.LastWriteTimeUtc >= DateTime.UtcNow.AddDays(-1)) continue;
            try { stale.Delete(true); } catch { }
        }
        var temporaryRoot = Path.Combine(basePath, requestId);
        if (Directory.Exists(temporaryRoot)) Directory.Delete(temporaryRoot, true);
        Directory.CreateDirectory(temporaryRoot);
        return temporaryRoot;
    }

    private static async Task<ArchiveBuildResult> CreateArchivesAsync(
        IEnumerable<CurrentFile> files,
        string temporaryRoot,
        ManualBackupRequest command,
        Func<int, long, bool, Task> progress,
        CancellationToken ct)
    {
        var groups = files
            .GroupBy(x => x.RootLabel, StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Select(x => new
            {
                RootLabel = x.Key,
                Files = (IReadOnlyList<CurrentFile>)x.OrderBy(
                    item => item.Relative,
                    StringComparer.OrdinalIgnoreCase).ToArray()
            })
            .ToArray();
        var artifacts = new List<ArchiveArtifact>();
        var skipped = new Dictionary<string, SkippedArchiveFile>(StringComparer.OrdinalIgnoreCase);
        var progressFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sourceBytes = groups.Sum(group => group.Files.Sum(item => item.ScannedLength));
        long progressBytesHighWater = 0;
        long committedBytes = 0;
        var buffer = new byte[1024 * 1024];

        foreach (var group in groups)
        {
            var parts = PartitionArchiveFiles(group.Files);
            for (var partIndex = 0; partIndex < parts.Count; partIndex++)
            {
                ct.ThrowIfCancellationRequested();
                var partFiles = parts[partIndex];
                var archiveName = parts.Count == 1
                    ? group.RootLabel + ".zip"
                    : $"{group.RootLabel}-part-{partIndex + 1:000}.zip";
                var archivePath = Path.Combine(temporaryRoot, archiveName);
                var buildingPath = archivePath + ".building";
                IReadOnlyList<CurrentFile> includedFiles;

                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    foreach (var item in partFiles.Where(item => !skipped.ContainsKey(item.File.FullName)))
                    {
                        if (!TryReadCurrentMetadata(item, out var reason))
                        {
                            skipped[item.File.FullName] = new(item, reason);
                            progressFiles.Add(item.File.FullName);
                        }
                    }

                    includedFiles = partFiles
                        .Where(item => !skipped.ContainsKey(item.File.FullName))
                        .ToArray();
                    if (File.Exists(buildingPath)) File.Delete(buildingPath);
                    var retry = false;
                    long attemptBytes = 0;
                    try
                    {
                        await using (var archiveStream = new FileStream(
                                         buildingPath,
                                         FileMode.CreateNew,
                                         FileAccess.ReadWrite,
                                         FileShare.None,
                                         buffer.Length,
                                         true))
                        using (var archive = new ZipArchive(archiveStream, ZipArchiveMode.Create, leaveOpen: false))
                        {
                            foreach (var item in includedFiles)
                            {
                                ct.ThrowIfCancellationRequested();
                                if (!TryReadCurrentMetadata(item, out var reason))
                                    throw new UnstableArchiveFileException(item, reason);

                                var entry = archive.CreateEntry(item.Relative.Replace('\\', '/'), CompressionLevel.Fastest);
                                FileStream source;
                                try
                                {
                                    source = new FileStream(
                                        item.File.FullName,
                                        FileMode.Open,
                                        FileAccess.Read,
                                        FileShare.ReadWrite | FileShare.Delete,
                                        buffer.Length,
                                        true);
                                }
                                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                                {
                                    throw new UnstableArchiveFileException(
                                        item,
                                        "Dosya sıkıştırma sırasında okunamadı: " + ex.Message);
                                }
                                await using (source)
                                {
                                    await using var destination = entry.Open();
                                    var remaining = item.ScannedLength;
                                    while (remaining > 0)
                                    {
                                        var requested = (int)Math.Min(buffer.Length, remaining);
                                        int read;
                                        try
                                        {
                                            read = await source.ReadAsync(buffer.AsMemory(0, requested), ct);
                                        }
                                        catch (IOException ex)
                                        {
                                            throw new UnstableArchiveFileException(
                                                item,
                                                "Dosya sıkıştırma sırasında okunamadı: " + ex.Message);
                                        }
                                        if (read == 0)
                                            throw new UnstableArchiveFileException(
                                                item,
                                                "Dosya sıkıştırma sırasında küçüldü veya kullanılamaz oldu.");
                                        await destination.WriteAsync(buffer.AsMemory(0, read), ct);
                                        remaining -= read;
                                        attemptBytes += read;
                                        progressBytesHighWater = Math.Min(
                                            sourceBytes,
                                            Math.Max(progressBytesHighWater, committedBytes + attemptBytes));
                                        await progress(progressFiles.Count, progressBytesHighWater, false);
                                    }
                                }

                                if (!TryReadCurrentMetadata(item, out reason))
                                    throw new UnstableArchiveFileException(item, reason);
                                progressFiles.Add(item.File.FullName);
                                await progress(progressFiles.Count, progressBytesHighWater, false);
                            }
                        }
                    }
                    catch (UnstableArchiveFileException ex)
                    {
                        skipped[ex.FileItem.File.FullName] = new(ex.FileItem, ex.Message);
                        progressFiles.Add(ex.FileItem.File.FullName);
                        retry = true;
                    }

                    if (retry)
                    {
                        if (File.Exists(buildingPath)) File.Delete(buildingPath);
                        continue;
                    }

                    using (var verification = ZipFile.OpenRead(buildingPath))
                    {
                        if (verification.Entries.Count != includedFiles.Count)
                            throw new InvalidDataException($"ZIP doğrulaması başarısız: {archivePath}");
                    }
                    File.Move(buildingPath, archivePath, true);
                    committedBytes += includedFiles.Sum(item => item.ScannedLength);
                    break;
                }
                artifacts.Add(new ArchiveArtifact(
                    new FileInfo(archivePath),
                    includedFiles,
                    group.RootLabel,
                    group.Files.FirstOrDefault()?.Root,
                    partIndex + 1,
                    parts.Count));
            }
        }

        var manifestPath = Path.Combine(temporaryRoot, "manifest.json");
        var manifest = new
        {
            formatVersion = 2,
            command.RequestId,
            command.SnapshotFolder,
            createdAtUtc = DateTime.UtcNow,
            archives = artifacts.Where(artifact => artifact.File.Extension.Equals(".zip", StringComparison.OrdinalIgnoreCase)).Select(artifact => new
            {
                fileName = artifact.File.Name,
                artifact.RootLabel,
                artifact.SourcePath,
                artifact.PartNumber,
                artifact.PartCount,
                files = artifact.SourceFiles.Select(item => new
                {
                    path = item.Relative.Replace('\\', '/'),
                    size = item.ScannedLength,
                    lastWriteAtUtc = new DateTime(item.ScannedLastWriteUtcTicks, DateTimeKind.Utc)
                })
            }),
            skippedFiles = skipped.Values.Select(item => new
            {
                sourcePath = item.Source.File.FullName,
                item.Source.RootLabel,
                path = item.Source.Relative.Replace('\\', '/'),
                item.Reason
            })
        };
        await File.WriteAllTextAsync(
            manifestPath,
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }),
            ct);
        artifacts.Add(new ArchiveArtifact(new FileInfo(manifestPath), []));
        return new ArchiveBuildResult(artifacts, skipped.Values.ToArray());
    }

    private static IReadOnlyList<IReadOnlyList<CurrentFile>> PartitionArchiveFiles(IReadOnlyList<CurrentFile> files)
    {
        var parts = new List<IReadOnlyList<CurrentFile>>();
        var current = new List<CurrentFile>();
        long currentBytes = 0;
        foreach (var item in files)
        {
            if (current.Count > 0 && currentBytes > ArchivePartTargetBytes - Math.Min(item.ScannedLength, ArchivePartTargetBytes))
            {
                parts.Add(current.ToArray());
                current = [];
                currentBytes = 0;
            }
            current.Add(item);
            currentBytes = currentBytes > long.MaxValue - item.ScannedLength
                ? long.MaxValue
                : currentBytes + item.ScannedLength;
        }
        if (current.Count > 0) parts.Add(current.ToArray());
        return parts;
    }

    private static bool TryReadCurrentMetadata(CurrentFile item, out string reason)
    {
        try
        {
            var current = new FileInfo(item.File.FullName);
            if (!current.Exists)
            {
                reason = "Dosya taramadan sonra kayboldu.";
                return false;
            }
            if (current.Length != item.ScannedLength ||
                current.LastWriteTimeUtc.Ticks != item.ScannedLastWriteUtcTicks)
            {
                reason = "Dosya taramadan sonra veya sıkıştırma sırasında değişti.";
                return false;
            }
            reason = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            reason = "Dosya taramadan sonra okunamadı: " + ex.Message;
            return false;
        }
    }

    private static string? BuildSkippedFilesWarning(IReadOnlyCollection<string> skippedFiles)
    {
        if (skippedFiles.Count == 0) return null;
        var examples = string.Join(" · ", skippedFiles.Take(3).Select(CompactReportedPath));
        var remainder = skippedFiles.Count > 3 ? $" · +{skippedFiles.Count - 3} dosya" : string.Empty;
        return $"{skippedFiles.Count} değişken dosya snapshot dışında bırakıldı: {examples}{remainder}";
    }

    private static string CompactReportedPath(string path)
    {
        const int maxLength = 1024;
        if (path.Length <= maxLength) return path;
        return "…" + path[^Math.Min(maxLength - 1, path.Length)..];
    }

    private static void DeleteTemporaryRoot(string? temporaryRoot)
    {
        if (string.IsNullOrWhiteSpace(temporaryRoot) || !Directory.Exists(temporaryRoot)) return;
        try { Directory.Delete(temporaryRoot, true); } catch { }
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
                    files[file.FullName] = new(
                        file,
                        root,
                        rootLabel,
                        relative,
                        file.Length,
                        file.LastWriteTimeUtc.Ticks);
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
        bool cancelled = false,
        int skippedFilesCount = 0,
        IReadOnlyList<string>? skippedFiles = null,
        string? warning = null,
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
            renameLikeChanges,
            cancelled,
            skippedFilesCount,
            skippedFiles = skippedFiles?.Take(20).Select(CompactReportedPath).ToArray() ?? [],
            warning
        }, JsonOptions());
        using var request = Authorized(HttpMethod.Post, new Uri(baseUri, "/api/server-controller/bridge/backup-result"), settings.ApiKey);
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        try { using var _ = await Http.SendAsync(request, ct); } catch { }
    }

    private async Task<bool> ReportProgressAsync(
        string requestId,
        string phase,
        int filesScanned,
        int filesUploaded,
        long bytesUploaded,
        long totalBytes,
        CancellationToken ct)
    {
        var settings = _config.Current.Hub;
        if (!TryBridgeBase(settings, out var baseUri)) return false;
        var payload = JsonSerializer.Serialize(new
        {
            serverId = _config.Current.ServerId,
            requestId,
            phase,
            filesScanned,
            filesUploaded,
            bytesUploaded,
            totalBytes
        }, JsonOptions());
        using var request = Authorized(
            HttpMethod.Post,
            new Uri(baseUri, "/api/server-controller/bridge/backup-progress"),
            settings.ApiKey);
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        try
        {
            using var response = await Http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return false;
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return document.RootElement.TryGetProperty("cancelRequested", out var cancelled) &&
                   cancelled.ValueKind == JsonValueKind.True;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
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

    private static async Task DeleteDropboxPathIfExistsAsync(
        string path,
        string accessToken,
        CancellationToken ct)
    {
        try
        {
            using var response = await DropboxJsonAsync(
                "https://api.dropboxapi.com/2/files/delete_v2",
                accessToken,
                new { path },
                ct);
            if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.Conflict) return;
        }
        catch
        {
            // Cancellation cleanup is best effort; the Hub request is still completed as cancelled.
        }
    }

    private static async Task UploadAsync(
        FileInfo file,
        string remotePath,
        string accessToken,
        int chunkSize,
        Func<long, Task> progress,
        CancellationToken ct)
    {
        const long maxFile = 2_199_019_061_248L;
        if (file.Length > maxFile) throw new IOException($"Dosya Dropbox sınırını aşıyor: {file.FullName}");
        var initialLength = file.Length;
        var initialTicks = file.LastWriteTimeUtc.Ticks;
        await using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, chunkSize, true);
        var buffer = new byte[chunkSize];
        var first = await ReadChunkAsync(stream, buffer, ct);
        using var start = await DropboxContentWithRetryAsync(
            "https://content.dropboxapi.com/2/files/upload_session/start",
            accessToken,
            new { close = false },
            buffer,
            first,
            "Dropbox yükleme oturumu başlatılamadı",
            ct);
        if (!start.IsSuccessStatusCode)
            throw await DropboxFailureAsync("Dropbox oturumu başlatılamadı", file, start, ct);
        using var startDoc = JsonDocument.Parse(await start.Content.ReadAsStringAsync(ct));
        var sessionId = startDoc.RootElement.GetProperty("session_id").GetString()
            ?? throw new IOException("Dropbox session id eksik.");
        long offset = first;
        await progress(offset);
        var reconciliationAttempts = 0;

        while (offset < initialLength)
        {
            stream.Position = offset;
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
            HttpResponseMessage response;
            try
            {
                response = await DropboxContentWithRetryAsync(
                    url,
                    accessToken,
                    arg,
                    buffer,
                    count,
                    $"Dropbox yüklemesi {FormatBytes(offset)} konumunda kesildi",
                    ct);
            }
            catch (IOException)
            {
                if (final && await DropboxFileMatchesLengthAsync(remotePath, initialLength, accessToken, ct))
                {
                    offset = initialLength;
                    await progress(offset);
                    break;
                }
                throw;
            }
            using (response)
            {
                if (response.IsSuccessStatusCode)
                {
                    offset += count;
                    reconciliationAttempts = 0;
                }
                else
                {
                    var error = await ReadDropboxErrorAsync(response, ct);
                    if (error.CorrectOffset is { } correctOffset &&
                        correctOffset >= 0 && correctOffset <= initialLength)
                    {
                        reconciliationAttempts++;
                        if (reconciliationAttempts > 3)
                            throw new IOException(
                                $"Dropbox yükleme konumu üç denemede uzlaştırılamadı: {file.FullName}");
                        offset = correctOffset;
                    }
                    else if (final && await DropboxFileMatchesLengthAsync(remotePath, initialLength, accessToken, ct))
                    {
                        offset = initialLength;
                    }
                    else
                    {
                        throw new IOException(
                            $"Dropbox yüklemesi başarısız: {file.FullName} (HTTP {(int)response.StatusCode}: {error.Summary})");
                    }
                }
            }
            await progress(offset);
        }

        if (initialLength <= first)
        {
            var arg = new
            {
                cursor = new { session_id = sessionId, offset },
                commit = new { path = remotePath, mode = "overwrite", autorename = false, mute = true, strict_conflict = false }
            };
            HttpResponseMessage? response = null;
            try
            {
                response = await DropboxContentWithRetryAsync(
                    "https://content.dropboxapi.com/2/files/upload_session/finish",
                    accessToken,
                    arg,
                    [],
                    0,
                    "Dropbox yüklemesi tamamlanamadı",
                    ct);
                if (!response.IsSuccessStatusCode &&
                    !await DropboxFileMatchesLengthAsync(remotePath, initialLength, accessToken, ct))
                    throw await DropboxFailureAsync("Dropbox yüklemesi tamamlanamadı", file, response, ct);
            }
            catch (IOException)
            {
                if (!await DropboxFileMatchesLengthAsync(remotePath, initialLength, accessToken, ct))
                    throw;
            }
            finally
            {
                response?.Dispose();
            }
            await progress(offset);
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

    private static async Task<HttpResponseMessage> DropboxContentWithRetryAsync(
        string url,
        string accessToken,
        object arg,
        byte[] buffer,
        int count,
        string operation,
        CancellationToken ct)
    {
        Exception? lastException = null;
        for (var attempt = 1; attempt <= DropboxUploadMaxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var response = await DropboxContentAsync(url, accessToken, arg, buffer, count, ct);
                if (response.IsSuccessStatusCode || !IsTransientDropboxStatus(response.StatusCode) ||
                    attempt == DropboxUploadMaxAttempts)
                    return response;

                var delay = DropboxRetryDelay(response, attempt);
                response.Dispose();
                await Task.Delay(delay, ct);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                lastException = new TimeoutException("Dropbox isteği zaman aşımına uğradı.");
                if (attempt == DropboxUploadMaxAttempts) break;
                await Task.Delay(DropboxRetryDelay(null, attempt), ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                lastException = ex;
                if (attempt == DropboxUploadMaxAttempts) break;
                await Task.Delay(DropboxRetryDelay(null, attempt), ct);
            }
        }

        throw new IOException(
            $"{operation}; {DropboxUploadMaxAttempts} denemeden sonra bağlantı kurulamadı: {lastException?.GetBaseException().Message}",
            lastException);
    }

    private static bool IsTransientDropboxStatus(HttpStatusCode status) =>
        status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)status >= 500;

    private static TimeSpan DropboxRetryDelay(HttpResponseMessage? response, int attempt)
    {
        var retryAfter = response?.Headers.RetryAfter?.Delta;
        if (retryAfter is { } requested && requested > TimeSpan.Zero)
            return requested > TimeSpan.FromSeconds(60) ? TimeSpan.FromSeconds(60) : requested;
        return TimeSpan.FromSeconds(Math.Min(30, 1 << Math.Min(attempt, 5)));
    }

    private static async Task<DropboxErrorInfo> ReadDropboxErrorAsync(
        HttpResponseMessage response,
        CancellationToken ct)
    {
        var raw = await response.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(raw))
            return new DropboxErrorInfo(response.ReasonPhrase ?? "Dropbox hatası", null);
        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            var summary = root.TryGetProperty("error_summary", out var summaryElement)
                ? summaryElement.GetString() ?? raw
                : raw;
            return new DropboxErrorInfo(summary, FindCorrectOffset(root));
        }
        catch (JsonException)
        {
            return new DropboxErrorInfo(raw, null);
        }
    }

    private static long? FindCorrectOffset(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.NameEquals("correct_offset") && property.Value.TryGetInt64(out var value))
                    return value;
                var nested = FindCorrectOffset(property.Value);
                if (nested.HasValue) return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = FindCorrectOffset(item);
                if (nested.HasValue) return nested;
            }
        }
        return null;
    }

    private static async Task<IOException> DropboxFailureAsync(
        string operation,
        FileInfo file,
        HttpResponseMessage response,
        CancellationToken ct)
    {
        var error = await ReadDropboxErrorAsync(response, ct);
        return new IOException($"{operation}: {file.FullName} (HTTP {(int)response.StatusCode}: {error.Summary})");
    }

    private static async Task<bool> DropboxFileMatchesLengthAsync(
        string remotePath,
        long expectedLength,
        string accessToken,
        CancellationToken ct)
    {
        try
        {
            using var response = await DropboxJsonAsync(
                "https://api.dropboxapi.com/2/files/get_metadata",
                accessToken,
                new { path = remotePath, include_media_info = false, include_deleted = false },
                ct);
            if (!response.IsSuccessStatusCode) return false;
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return document.RootElement.TryGetProperty(".tag", out var tag) && tag.GetString() == "file" &&
                   document.RootElement.TryGetProperty("size", out var size) && size.TryGetInt64(out var length) &&
                   length == expectedLength;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private State LoadState()
    {
        if (!File.Exists(_statePath)) return new();
        try
        {
            var state = JsonSerializer.Deserialize<State>(File.ReadAllText(_statePath), JsonOptions())
                ?? throw new InvalidDataException("Yerel yedekleme durumu boş.");
            state.Files ??= [];
            state.LastSkippedFiles ??= [];
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
