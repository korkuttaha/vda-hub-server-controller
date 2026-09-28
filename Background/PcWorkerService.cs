using Microsoft.Extensions.Hosting;
using VdaHubServerController.Services;

namespace VdaHubServerController.Background;

public sealed class PcWorkerService(PcAgentEngine engine) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        engine.RunAsync(stoppingToken);
}
