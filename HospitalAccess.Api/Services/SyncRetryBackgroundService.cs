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
                var now = DateTime.UtcNow;
                var pending = await db.SyncStatuses
                    .Where(SyncRetryPolicy.EligibleForAutoRetry(now))
                    .Select(s => s.UserId)
                    .Distinct()
                    .ToListAsync(stoppingToken);

                var enqueued = _queue.EnqueueMany(pending);

                // Raio-X da varredura: sem isto, pendências em backoff/quarentena eram invisíveis —
                // o painel mostrava "N sincronizações pendentes" e NADA aparecia no log, parecendo
                // que o sistema simplesmente não sincronizava.
                var snapshot = await db.SyncStatuses
                    .Where(s => s.State == Domain.Enums.SyncState.Pending || s.State == Domain.Enums.SyncState.Failed)
                    .GroupBy(_ => 1)
                    .Select(g => new
                    {
                        Pending = g.Count(s => s.State == Domain.Enums.SyncState.Pending),
                        DueNow = g.Count(s => s.State == Domain.Enums.SyncState.Failed && s.NextRetryAtUtc != null && s.NextRetryAtUtc <= now),
                        WaitingBackoff = g.Count(s => s.State == Domain.Enums.SyncState.Failed && s.NextRetryAtUtc != null && s.NextRetryAtUtc > now),
                        NextRetryAt = g.Where(s => s.State == Domain.Enums.SyncState.Failed && s.NextRetryAtUtc != null && s.NextRetryAtUtc > now)
                            .Min(s => s.NextRetryAtUtc),
                        Quarantined = g.Count(s => s.State == Domain.Enums.SyncState.Failed && s.NextRetryAtUtc == null),
                    })
                    .FirstOrDefaultAsync(stoppingToken);

                if (snapshot is not null &&
                    (snapshot.Pending + snapshot.DueNow + snapshot.WaitingBackoff + snapshot.Quarantined) > 0)
                {
                    _logger.LogInformation(
                        "Sincronização: {Pending} pendente(s), {Due} falha(s) elegível(is) agora, {Waiting} aguardando backoff{Next}, {Quarantined} em quarentena (erro permanente — resolver pela tela de usuários); {Enqueued} usuário(s) reenfileirado(s) nesta varredura.",
                        snapshot.Pending, snapshot.DueNow, snapshot.WaitingBackoff,
                        snapshot.NextRetryAt is { } next ? $" (próxima às {next:HH:mm:ss} UTC)" : "",
                        snapshot.Quarantined, enqueued);
                }
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
