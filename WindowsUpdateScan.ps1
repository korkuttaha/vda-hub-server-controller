# Read-only Windows Update Agent query. Never download/install updates or restart Windows.
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
$session = $null
$searcher = $null
$result = $null
$system = $null
try {
    $session = New-Object -ComObject Microsoft.Update.Session
    $session.ClientApplicationID = 'VDAKor Server Windows Update Monitor'
    $searcher = $session.CreateUpdateSearcher()
    # Keep the configured source (including WSUS); do not bypass organization policy.
    $searcher.ServerSelection = 0
    $searcher.Online = $true
    $result = $searcher.Search("IsInstalled=0 and IsHidden=0 and Type='Software'")
    if ([int]$result.ResultCode -ne 2) { throw 'Windows Update scan was incomplete.' }
    $securityCount = 0
    $items = @()
    for ($index = 0; $index -lt $result.Updates.Count; $index++) {
        $update = $null
        try {
            $update = $result.Updates.Item($index)
            $security = -not [string]::IsNullOrWhiteSpace([string]$update.MsrcSeverity)
            foreach ($category in $update.Categories) {
                try {
                    if ($category.CategoryID -eq 'e6cf1350-c01b-414d-a61f-263d14d133b4') { $security = $true }
                } finally { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($category) }
            }
            if ($security) { $securityCount++ }
            $title = [string]$update.Title
            $items += [pscustomobject]@{
                updateId = [string]$update.Identity.UpdateID
                title = $title.Substring(0, [Math]::Min(300, $title.Length))
                kbArticleIds = @($update.KBArticleIDs | Select-Object -First 8 | ForEach-Object { ([string]$_).Substring(0, [Math]::Min(24, ([string]$_).Length)) })
                severity = [string]$update.MsrcSeverity
                isSecurity = $security
            }
        } finally { if ($null -ne $update) { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($update) } }
    }
    $system = New-Object -ComObject Microsoft.Update.SystemInfo
    $now = [DateTime]::UtcNow.ToString('o')
    @{
        status = 'success'; checkedAtUtc = $now; lastSuccessfulScanAtUtc = $now
        pendingCount = [int]$result.Updates.Count; securityCount = $securityCount
        rebootRequired = [bool]$system.RebootRequired; error = $null
        updatesTruncated = $items.Count -gt 100
        updates = @($items | Sort-Object @{ Expression = 'isSecurity'; Descending = $true }, title | Select-Object -First 100)
    } | ConvertTo-Json -Depth 6 -Compress
} catch {
    # Report failure explicitly. An unreachable source must never become '0 pending'.
    @{ status = 'error'; checkedAtUtc = [DateTime]::UtcNow.ToString('o'); error = 'Windows Update scan failed.'; updates = @() } | ConvertTo-Json -Compress
} finally {
    foreach ($com in @($system, $result, $searcher, $session)) {
        if ($null -ne $com) { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($com) }
    }
}
