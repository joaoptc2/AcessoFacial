using HospitalAccess.Domain.Enums;
using HospitalAccess.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HospitalAccess.Api.Services;

/// <summary>
/// Reprocessa periodicamente as sincronizações pendentes/falhas: busca os usuários com
/// DeviceSyncStatus Pending/Failed e os REENFILEIRA na fila serial (<see cref="UserSyncQueue"/>),
/// que os processa um de cada vez. Sem isto, um usuário cadastrado/revogado enquanto um controlador
/// estava offline ficaria para sempre em Pending/Failed. A dedup da fila evita empilhar repetidos.
/// </summary>
public sealed class SyncRetryBackgroundService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(2);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IUserSyncQueue _queue;
    private readonly ILogger<SyncRetryBackgroundService> _logger;

    public SyncRetryBackgroundService(IServiceScopeFactory scopeFactory, IUserSyncQueue queue, ILogger<SyncRetryBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _queue = queue;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AccessDbContext>();
                var pending = await db.SyncStatuses
                    .Where(s => s.State == SyncState.Pending || s.State == SyncState.Failed)
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
