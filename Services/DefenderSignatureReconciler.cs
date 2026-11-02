using System.Text.RegularExpressions;
using VdaHubServerController.Models;

namespace VdaHubServerController.Services;

// A WUA result may outlive a signature update installed independently by Defender.
public static class DefenderSignatureReconciler
{
    public static bool IsSignature(WindowsUpdateItem item) => item.KbArticleIds.Count > 0 &&
        item.KbArticleIds.All(kb => string.Equals(kb, "2267602", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(kb, "KB2267602", StringComparison.OrdinalIgnoreCase));

    public static WindowsUpdateSnapshot Reconcile(WindowsUpdateSnapshot snapshot, string? installedVersion)
    {
        if (snapshot.Status != "success" || !TryVersion(installedVersion, out var installed)) return snapshot;
        var removed = snapshot.Updates.Where(item => IsSignature(item) && IsInstalled(item.Title, installed!)).ToList();
        if (removed.Count == 0 || snapshot.PendingCount < removed.Count || snapshot.PendingCount is null ||
            snapshot.SecurityCount is null || snapshot.SecurityCount < removed.Count(item => item.IsSecurity)) return snapshot;
        return snapshot with
        {
            PendingCount = snapshot.PendingCount - removed.Count,
            SecurityCount = snapshot.SecurityCount - removed.Count(item => item.IsSecurity),
            Updates = snapshot.Updates.Where(item => !removed.Contains(item)).ToList()
        };
    }

    private static bool IsInstalled(string title, Version installed)
    {
        // Do not depend on localized words such as "Version". Ambiguous titles stay pending.
        var matches = Regex.Matches(title, @"(?<![\d.])\d+\.\d+\.\d+\.\d+(?![\d.])", RegexOptions.CultureInvariant);
        return matches.Count == 1 && TryVersion(matches[0].Value, out var offered) && installed >= offered;
    }

    private static bool TryVersion(string? text, out Version? version)
    {
        version = null;
        return text is { Length: > 0 and <= 64 } &&
            Regex.IsMatch(text, @"^[1-9][0-9]*\.[0-9]+\.[0-9]+\.[0-9]+$", RegexOptions.CultureInvariant) &&
            Version.TryParse(text, out version);
    }
}
