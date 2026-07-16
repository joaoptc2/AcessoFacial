using HospitalAccess.Api.Options;
using HospitalAccess.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HospitalAccess.Api.Services;

/// <summary>
/// Reprocessa periodicamente as sincronizações ELEGÍVEIS: Pending sempre; Failed só quando o
/// backoff exponencial venceu (<see cref="SyncRetryPolicy"/>) — falhas permanentes (foto sem
/// rosto, duplicidade) ficam em quarentena até ação manual. Os elegíveis são REENFILEIRADOS na
/// fila serial (<see cref="UserSyncQueue"/>), que os processa um de cada vez por usuário.
/// Sem a elegibilidade, TODO Pending/Failed re-enviava a face completa a cada varredura,
/// para sempre — a principal fonte de sobrecarga de rede nos controladores.
/// </summary>
public sealed class SyncRetryBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IUserSyncQueue _queue;
    private readonly SyncRetryOptions _options;
    private readonly ILogger<SyncRetryBackgroundService> _logger;

    public SyncRetryBackgroundService(IServiceScopeFactory scopeFactory, IUserSyncQueue queue,
        IOptions<SyncRetryOptions> options, ILogger<SyncRetryBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _queue = queue;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Math.Max: config 0/negativa não pode derrubar o host (PeriodicTimer exige período > 0).
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, _options.ScanIntervalSeconds)));
        do
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AccessDbContext>();
                var pending = await db.SyncStatuses
                    .Where(SyncRetryPolicy.EligibleForAutoRetry(DateTime.UtcNow))
                    .Select(s => s.UserId)
                    .Distinct()
                    .ToListAsync(stoppingToken);

                var enqueued = _queue.EnqueueMany(pending);
                if (enqueued > 0)
                    _logger.LogInformation("Retry de sincronização: {Count} usuário(s) reenfileirado(s).", enqueued);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha ao reprocessar sincronizações pendentes.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
