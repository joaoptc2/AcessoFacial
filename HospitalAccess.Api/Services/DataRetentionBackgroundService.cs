using HospitalAccess.Domain.Entities;
using HospitalAccess.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HospitalAccess.Api.Services;

/// <summary>
/// Expurgo de dados conforme a política de retenção configurada em SystemSettings (LGPD). Roda
/// uma vez por dia. Um prazo 0 significa "reter indefinidamente" (nada é apagado). Fotos de face
/// de usuários NÃO são expurgadas aqui — elas são removidas ao excluir o usuário.
/// </summary>
public sealed class DataRetentionBackgroundService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DataRetentionBackgroundService> _logger;

    public DataRetentionBackgroundService(IServiceScopeFactory scopeFactory, ILogger<DataRetentionBackgroundService> logger)
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
                await PurgeAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha no expurgo de dados por retenção.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task PurgeAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AccessDbContext>();

        // Por chave (linha única) — evita o warning de EF "First without OrderBy".
        var settings = await db.SystemSettings.AsNoTracking()
                           .FirstOrDefaultAsync(s => s.Id == SystemSettings.SingletonId, ct)
                       ?? new SystemSettings();
        var now = DateTime.UtcNow;

        var photos = await PurgeOlderThanAsync(db.EventPhotos, p => p.CapturedAtUtc, settings.EventPhotoRetentionDays, now, ct);
        var access = await PurgeOlderThanAsync(db.AccessLogs, l => l.TimestampUtc, settings.AccessLogRetentionDays, now, ct);
        var alarms = await PurgeOlderThanAsync(db.AlarmEvents, a => a.TimestampUtc, settings.AlarmLogRetentionDays, now, ct);
        var audits = await PurgeOlderThanAsync(db.ControllerAuditLogs, a => a.TimestampUtc, settings.ControllerAuditRetentionDays, now, ct);

        if (photos + access + alarms + audits > 0)
        {
            _logger.LogInformation(
                "Expurgo por retenção: {Photos} fotos, {Access} acessos, {Alarms} alarmes, {Audits} auditorias.",
                photos, access, alarms, audits);
        }
    }

    private static async Task<int> PurgeOlderThanAsync<TEntity>(
        DbSet<TEntity> set, System.Linq.Expressions.Expression<Func<TEntity, DateTime>> timestamp,
        int retentionDays, DateTime now, CancellationToken ct) where TEntity : class
    {
        if (retentionDays <= 0) return 0; // 0 = reter indefinidamente
        var cutoff = now.AddDays(-retentionDays);

        // Compara timestamp < cutoff via ExecuteDelete (sem carregar as linhas em memória).
        var param = timestamp.Parameters[0];
        var body = System.Linq.Expressions.Expression.LessThan(
            timestamp.Body, System.Linq.Expressions.Expression.Constant(cutoff));
        var predicate = System.Linq.Expressions.Expression.Lambda<Func<TEntity, bool>>(body, param);

        return await set.Where(predicate).ExecuteDeleteAsync(ct);
    }
}
