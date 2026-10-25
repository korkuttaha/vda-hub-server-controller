$ErrorActionPreference = 'Stop'

# Compile the actual polling engine with a fake power service. No HTTP request or
# Windows power command is executed by these regression checks.
$engine = Get-Content (Join-Path $PSScriptRoot '../Services/PcAgentEngine.cs') -Raw
$source = @'
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Http;
'@ + [Environment]::NewLine + $engine + [Environment]::NewLine + @'
public sealed record PcAgentCycleResult(bool CommandReceived, bool Success, string Message);

public sealed class PcPowerCommandService
{
    private readonly Func<CancellationToken, Task<PcAgentCycleResult>> _poll;

    public PcPowerCommandService(Func<CancellationToken, Task<PcAgentCycleResult>> poll)
        => _poll = poll;

    public Task<PcAgentCycleResult> PollAndRunAsync(CancellationToken ct) => _poll(ct);
}

public static class ReconnectChecks
{
    public static async Task RunAsync()
    {
        await RetriesAfterAsync(new TaskCanceledException("Simulated HttpClient timeout"));
        await RetriesAfterAsync(new OperationCanceledException("Request-only cancellation"));
        await RetriesAfterAsync(new HttpRequestException("Temporary network failure"));
        await StopsForHostCancellationAsync();
        await StopsDuringRetryDelayAsync();
        Console.WriteLine("PASS: all 5 PC Agent reconnect/cancellation checks.");
    }

    private static async Task RetriesAfterAsync(Exception failure)
    {
        using var stop = new CancellationTokenSource();
        var calls = 0;
        var power = new PcPowerCommandService(ct =>
        {
            if (++calls == 1)
                return Task.FromException<PcAgentCycleResult>(failure);
            stop.Cancel();
            return Task.FromResult(new PcAgentCycleResult(false, true, "Reconnected"));
        });

        await new PcAgentEngine(power).RunAsync(stop.Token).WaitAsync(TimeSpan.FromSeconds(15));
        if (calls != 2)
            throw new Exception(failure.GetType().Name + " stopped polling instead of reconnecting.");
    }

    private static async Task StopsForHostCancellationAsync()
    {
        using var stop = new CancellationTokenSource();
        var calls = 0;
        var power = new PcPowerCommandService(ct =>
        {
            calls++;
            stop.Cancel();
            return Task.FromCanceled<PcAgentCycleResult>(ct);
        });

        await new PcAgentEngine(power).RunAsync(stop.Token).WaitAsync(TimeSpan.FromSeconds(2));
        if (calls != 1)
            throw new Exception("Host cancellation incorrectly retried polling.");
    }

    private static async Task StopsDuringRetryDelayAsync()
    {
        using var stop = new CancellationTokenSource();
        var power = new PcPowerCommandService(ct =>
            Task.FromException<PcAgentCycleResult>(new HttpRequestException("Offline")));

        var running = new PcAgentEngine(power).RunAsync(stop.Token);
        stop.Cancel();
        // Cancellation during backoff must finish cleanly, without a faulted task.
        await running.WaitAsync(TimeSpan.FromSeconds(2));
    }
}
'@

Add-Type -TypeDefinition $source
[VdaHubServerController.Services.ReconnectChecks]::RunAsync().GetAwaiter().GetResult()
