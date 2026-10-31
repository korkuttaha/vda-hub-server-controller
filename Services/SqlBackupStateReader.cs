using System.Text.Json;
using VdaHubServerController.Models;

namespace VdaHubServerController.Services;

/// <summary>Reads only the bounded scheduler state, never plan.json or raw logs.</summary>
public static class SqlBackupStateReader
{
    public static SqlBackupServiceSnapshot Read(SqlBackupServiceSnapshot snapshot, string commonDataDirectory)
    {
        try
        {
            var directory = Path.Combine(commonDataDirectory, "VDA.MssqlBackup.Service");
            var stateDirectory = Path.Combine(directory, "state");
            var path = Path.Combine(stateDirectory, "schedule-state.json");
            foreach (var component in new[] { commonDataDirectory, directory, stateDirectory, path })
            {
                if (!Path.Exists(component)) return snapshot with { ScheduleState = "missing" };
                if ((File.GetAttributes(component) & FileAttributes.ReparsePoint) != 0)
                    return snapshot with { ScheduleState = "error" };
            }
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (file.Length is <= 0 or > 65536) return snapshot with { ScheduleState = "error" };
            var bytes = new byte[65537];
            var count = 0;
            while (count < bytes.Length)
            {
                var read = file.Read(bytes, count, bytes.Length - count);
                if (read == 0) break;
                count += read;
            }
            if (count > 65536) return snapshot with { ScheduleState = "error" };
            using var document = JsonDocument.Parse(bytes.AsMemory(0, count), new JsonDocumentOptions { MaxDepth = 8 });
            var state = document.RootElement;
            if (state.ValueKind != JsonValueKind.Object || !state.TryGetProperty("NextRunUtc", out var next) ||
                !next.TryGetDateTimeOffset(out var nextRun) || nextRun == default)
                return snapshot with { ScheduleState = "error" };
            var outcome = state.TryGetProperty("LastOutcome", out var result) && result.ValueKind == JsonValueKind.String
                ? result.GetString() : null;
            var safeOutcome = outcome switch
            {
                "Başarılı" => "succeeded",
                "Hata" or "Hatalı tamamlandı" => "failed",
                "İptal edildi" => "cancelled",
                "Başlatıldı" => "started",
                "Önceki çalışma kesintiye uğradı" => "interrupted",
                "Yeni plan hazır" => "ready",
                "Plan devre dışı" => "disabled",
                _ => "unknown"
            };
            return snapshot with
            {
                ScheduleState = "available",
                NextRunUtc = nextRun.ToUniversalTime(),
                LastAttemptUtc = Timestamp(state, "LastAttemptUtc", snapshot.CheckedAtUtc),
                LastCompletedUtc = Timestamp(state, "LastCompletedUtc", snapshot.CheckedAtUtc),
                LastOutcome = safeOutcome,
                InProgress = state.TryGetProperty("InProgress", out var progress) && progress.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? progress.GetBoolean() : null
            };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException or System.Security.SecurityException)
        {
            return snapshot with { ScheduleState = "error" };
        }
    }

    private static DateTimeOffset? Timestamp(JsonElement state, string name, DateTime nowUtc) =>
        state.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
        value.TryGetDateTimeOffset(out var timestamp) && timestamp != default && timestamp <= new DateTimeOffset(nowUtc).AddMinutes(5)
            ? timestamp.ToUniversalTime() : null;
}
