namespace VdaHubServerController.Services;

public sealed class PcAgentEngine
{
    private readonly PcPowerCommandService _power;

    public PcAgentEngine(PcPowerCommandService power)
    {
        _power = power;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        Console.WriteLine("[VDAKor PC Agent] Arka plan bağlantısı başlatıldı.");
        while (!ct.IsCancellationRequested)
        {
            var delay = TimeSpan.FromSeconds(5);
            try
            {
                var result = await _power.PollAndRunAsync(ct);
                if (result.CommandReceived)
                    Console.WriteLine($"[VDAKor PC Agent] {(result.Success ? "OK" : "HATA")}: {result.Message}");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // HttpClient timeouts also throw TaskCanceledException. Only the host token
                // can stop this worker; a transient timeout must leave polling alive.
                Console.Error.WriteLine("[VDAKor PC Agent] Hub bağlantı hatası: " + ex.Message);
                delay = TimeSpan.FromSeconds(10);
            }

            try
            {
                await Task.Delay(delay, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
