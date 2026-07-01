using HospitalAccess.Application.Sync;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HospitalAccess.Api.Services;

/// <summary>Executa IVisitorExpirationJob periodicamente.</summary>
public sealed class VisitorExpirationBackgroundService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<VisitorExpirationBackgroundService> _logger;

    public VisitorExpirationBackgroundService(IServiceScopeFactory scopeFactory, ILogger<VisitorExpirationBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            using var scope = _scopeFactory.CreateScope();
            var job = scope.ServiceProvider.GetRequiredService<IVisitorExpirationJob>();
            try
            {
                await job.RunAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha ao executar VisitorExpirationJob.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
