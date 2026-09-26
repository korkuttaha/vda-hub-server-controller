using Microsoft.Extensions.Hosting;
using VdaHubServerController.Services;

namespace VdaHubServerController.Background;

public class WorkerService : BackgroundService
{
    private readonly ControllerEngine _engine;

    public WorkerService(ControllerEngine engine)
    {
        _engine = engine;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _engine.StartLoopAsync(stoppingToken);
    }
}
