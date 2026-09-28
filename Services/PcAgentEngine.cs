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
            try
            {
                var result = await _power.PollAndRunAsync(ct);
                if (result.CommandReceived)
                    Console.WriteLine($"[VDAKor PC Agent] {(result.Success ? "OK" : "HATA")}: {result.Message}");
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
            catch (TaskCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[VDAKor PC Agent] Hub bağlantı hatası: " + ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
            }
        }
    }
}
