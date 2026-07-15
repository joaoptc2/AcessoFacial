using HospitalAccess.Application.Sync;
using HospitalAccess.Gateway;
using HospitalAccess.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HospitalAccess.Api.Services;

/// <summary>
/// Consome a <see cref="UserSyncQueue"/> processando UM item por vez (envio "por partes"), com uma
/// pequena folga entre comandos, para não sobrecarregar os controladores durante lotes de cadastro.
/// Cada item roda em seu próprio escopo de DI (DbContext scoped).
/// </summary>
public sealed class UserSyncQueueWorker : BackgroundService
{
    // Folga entre comandos: dá respiro ao controlador entre uploads de face pesados.
    private static readonly TimeSpan GapBetweenItems = TimeSpan.FromMilliseconds(200);

    private readonly UserSyncQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<UserSyncQueueWorker> _logger;

    public UserSyncQueueWorker(UserSyncQueue queue, IServiceScopeFactory scopeFactory, ILogger<UserSyncQueueWorker> logger)
    {
        _queue = queue;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var work in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                switch (work)
                {
                    case SyncUserWork w:
                        _queue.MarkDequeued(w.UserId); // libera a dedup antes de processar (nova edição reenfileira)
                        await scope.ServiceProvider.GetRequiredService<IUserSyncService>().SyncUserAsync(w.UserId, stoppingToken);
                        break;
                    case RevokeUserWork w:
                        await scope.ServiceProvider.GetRequiredService<IUserSyncService>().RevokeUserAsync(w.UserId, stoppingToken);
                        break;
                    case RevokeDeletedUserWork w:
                        await RevokeDeletedAsync(scope.ServiceProvider, w, stoppingToken);
                        break;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha ao processar item de sincronização da fila.");
            }

            try { await Task.Delay(GapBetweenItems, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    /// <summary>Revoga no hardware um usuário já excluído do banco (linhas de Users/DeviceSyncStatus já não existem).</summary>
    private async Task RevokeDeletedAsync(IServiceProvider provider, RevokeDeletedUserWork work, CancellationToken ct)
    {
        var db = provider.GetRequiredService<AccessDbContext>();
        var gateway = provider.GetRequiredService<IDeviceGateway>();

        foreach (var controllerId in work.ControllerIds)
        {
            try
            {
                var controller = await db.Controllers.FirstOrDefaultAsync(c => c.Id == controllerId, ct);
                if (controller is null) continue;
                await gateway.DeletePersonAsync(controller, work.UserCode, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha ao revogar usuário {UserCode} no controlador {ControllerId}.", work.UserCode, controllerId);
            }
        }
    }
}
