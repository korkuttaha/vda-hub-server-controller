using System.Diagnostics;
using System.Text;
using System.Text.Json;
using VdaHubServerController.Models;

namespace VdaHubServerController.Services;

public static class WindowsUpdateScanner
{
    public static async Task<WindowsUpdateSnapshot> ScanAsync(CancellationToken ct)
    {
        using var resource = typeof(WindowsUpdateScanner).Assembly.GetManifestResourceStream(
            "VdaHubServerController.WindowsUpdateScan.ps1") ?? throw new InvalidOperationException("Missing Windows Update scanner.");
        using var reader = new StreamReader(resource, Encoding.UTF8);
        var script = await reader.ReadToEndAsync(ct);
        var output = await RunPowerShellAsync(script, TimeSpan.FromMinutes(4), ct);
        return JsonSerializer.Deserialize<WindowsUpdateSnapshot>(output,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidDataException("Empty Windows Update result.");
    }

    public static async Task<string?> ReadDefenderSignatureVersionAsync(CancellationToken ct)
    {
        const string script = """
            $ErrorActionPreference = 'Stop'
            [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
            Get-CimInstance -Namespace 'root/Microsoft/Windows/Defender' -ClassName MSFT_MpComputerStatus -OperationTimeoutSec 5 |
                Select-Object -ExpandProperty AntivirusSignatureVersion
            """;
        var output = await RunPowerShellAsync(script, TimeSpan.FromSeconds(15), ct);
        return output.Trim();
    }

    private static async Task<string> RunPowerShellAsync(string script, TimeSpan limit, CancellationToken ct)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Windows Update scanner could not start.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(limit);
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            var output = await stdout;
            await stderr;
            if (process.ExitCode != 0 || output.Length > 256 * 1024)
                throw new InvalidDataException("Windows Update scanner failed.");
            return output;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("Windows status query exceeded its time limit.");
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            // Observe both reads even on timeout/cancellation; never leave a WUA helper alive.
            try { await Task.WhenAll(stdout, stderr); }
            catch (OperationCanceledException) { }
        }
    }
}
