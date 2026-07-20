using HospitalAccess.Api.Options;
using HospitalAccess.Application.Sync;
using HospitalAccess.Gateway;
using HospitalAccess.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HospitalAccess.Api.Services;

/// <summary>
/// Consome a <see cref="UserSyncQueue"/> processando UM item por vez (envio "por partes"), com uma
/// pequena folga entre comandos, para não sobrecarregar os controladores durante lotes de cadastro.
/// Cada item roda em seu próprio escopo de DI (DbContext scoped).
/// </summary>
public sealed class UserSyncQueueWorker : BackgroundService
{
    // Workers paralelos: como a serialização real é POR CONTROLADOR (lock no gateway), vários usuários
    // podem sincronizar ao mesmo tempo em controladores diferentes — um controlador lento não trava os
    // demais. É trabalho de I/O (rede), não de CPU; o default 8 cobre bem um parque de ~30 aparelhos
    // (SyncQueue:WorkerCount).
    private readonly int _workerCount;

    private readonly UserSyncQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<UserSyncQueueWorker> _logger;

    public UserSyncQueueWorker(UserSyncQueue queue, IServiceScopeFactory scopeFactory,
        IOptions<SyncQueueOptions> options, ILogger<UserSyncQueueWorker> logger)
    {
        _queue = queue;
        _scopeFactory = scopeFactory;
        _workerCount = Math.Max(1, options.Value.WorkerCount);
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workers = Enumerable.Range(0, _workerCount).Select(_ => WorkerLoopAsync(stoppingToken));
        await Task.WhenAll(workers);
    }

    private async Task WorkerLoopAsync(CancellationToken stoppingToken)
    {
        await foreach (var work in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                switch (work)
                {
                    case SyncUserWork w:
                        // CompleteSync só no fim: garante exclusão por usuário (nenhum outro worker pega
                        // o mesmo usuário) e reenfileira se surgiu um pedido de re-sync durante o processo.
                        _queue.BeginProcessing(w.UserId);
                        try { await scope.ServiceProvider.GetRequiredService<IUserSyncService>().SyncUserAsync(w.UserId, stoppingToken); }
                        finally { _queue.CompleteSync(w.UserId); }
                        break;
                    case RevokeUserWork w:
                        await scope.ServiceProvider.GetRequiredService<IUserSyncService>().RevokeUserAsync(w.UserId, stoppingToken);
                        break;
                    case RevokeDeletedUserWork w:
                        await RevokeDeletedAsync(scope.ServiceProvider, w, stoppingToken);
                        break;
                    case ChangeVisitorRoomWork w:
                        await ChangeRoomAsync(scope.ServiceProvider, w, stoppingToken);
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
        }
    }

    /// <summary>
    /// Troca de quarto de um visitante: (1) limpa o QR/pessoa do(s) quarto(s) antigo(s) via HTTP —
    /// best-effort, o QR antigo deixa de abrir a porta; (2) REENFILEIRA a sincronização pela
    /// dedup por usuário (nunca dois workers no mesmo usuário — chamar SyncUserAsync direto aqui
    /// correria por fora dessa exclusão e podia colidir com um SyncUserWork simultâneo no índice
    /// único de DeviceSyncStatus). A reconciliação do sync revoga via SDK os controladores fora
    /// da lista desejada (os status antigos permanecem 'Synced' até lá) e cadastra a pessoa no
    /// quarto novo, gerando o QR novo.
    /// </summary>
    private async Task ChangeRoomAsync(IServiceProvider provider, ChangeVisitorRoomWork work, CancellationToken ct)
    {
        if (work.OldControllerIds.Count > 0)
        {
            try
            {
                var deviceQr = provider.GetRequiredService<DeviceQrService>();
                await deviceQr.RemoveFromControllersAsync(work.UserCode, work.OldControllerIds, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha ao limpar QR do quarto antigo do visitante {UserCode}.", work.UserCode);
            }
        }

        _queue.EnqueueSync(work.UserId);
    }

    /// <summary>Revoga no hardware um usuário já excluído do banco (linhas de Users/DeviceSyncStatus já não existem).</summary>
    private async Task RevokeDeletedAsync(IServiceProvider provider, RevokeDeletedUserWork work, CancellationToken ct)
    {
        var db = provider.GetRequiredService<AccessDbContext>();
        var gateway = provider.GetRequiredService<IDeviceGateway>();

        // Remove TAMBÉM pelo painel HTTP (People/Delete) — caminho validado em hardware que
        // remove pessoa+QR. Best-effort (só age onde há ApiBaseUrl); o SDK delete verificado
        // roda em seguida de qualquer forma.
        try
        {
            var deviceQr = provider.GetRequiredService<DeviceQrService>();
            await deviceQr.RemoveFromControllersAsync(work.UserCode, work.ControllerIds, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha ao remover usuário excluído {UserCode} via painel HTTP (seguindo com o SDK).", work.UserCode);
        }

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
