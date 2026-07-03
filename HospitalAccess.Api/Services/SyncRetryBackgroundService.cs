using HospitalAccess.Application.Sync;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HospitalAccess.Api.Services;

/// <summary>
/// Reprocessa periodicamente as sincronizações pendentes/falhas (IUserSyncService.RetryPendingAsync).
/// Sem este serviço, um usuário cadastrado/revogado enquanto um controlador estava offline ficaria
/// para sempre em Pending/Failed — a "fila de retry" documentada nunca era executada por ninguém.
/// </summary>
public sealed class SyncRetryBackgroundService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(2);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SyncRetryBackgroundService> _logger;

    public SyncRetryBackgroundService(IServiceScopeFactory scopeFactory, ILogger<SyncRetryBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
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
                var sync = scope.ServiceProvider.GetRequiredService<IUserSyncService>();
                await sync.RetryPendingAsync(stoppingToken);
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
