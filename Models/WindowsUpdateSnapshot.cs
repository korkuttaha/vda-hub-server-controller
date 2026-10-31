namespace VdaHubServerController.Models;

public sealed record WindowsUpdateSnapshot
{
    public string Status { get; init; } = "error";
    public DateTime CheckedAtUtc { get; init; }
    public DateTime? LastSuccessfulScanAtUtc { get; init; }
    public int? PendingCount { get; init; }
    public int? SecurityCount { get; init; }
    public bool? RebootRequired { get; init; }
    public string? Error { get; init; }
    public bool UpdatesTruncated { get; init; }
    public List<WindowsUpdateItem> Updates { get; init; } = [];
}

public sealed record WindowsUpdateItem
{
    public string UpdateId { get; init; } = "";
    public string Title { get; init; } = "";
    public List<string> KbArticleIds { get; init; } = [];
    public string? Severity { get; init; }
    public bool IsSecurity { get; init; }
}
