using HospitalAccess.Api.Options;
using HospitalAccess.Domain.Entities;
using HospitalAccess.Gateway;
using HospitalAccess.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HospitalAccess.Api.Services;

/// <summary>
/// Coleta periodicamente os registros de acesso armazenados nos controladores (Classe VIII) que
/// não chegaram pelo push em tempo real — recupera eventos ocorridos com o servidor offline.
/// Deduplica contra o AccessLog por (SN do controlador + nº de série do registro). Defesa em
/// profundidade sobre o monitoramento em tempo real. Não validado contra hardware real.
///
/// Intervalo em OfflineCollection:IntervalMinutes. Com OfflineCollection:SkipWhenPushHealthy
/// (opt-in), controladores com push de EVENTO recente são pulados — só ligar depois de validar
/// em hardware que o push avança o ponteiro de leitura do aparelho (senão a varredura re-baixa
/// os mesmos registros a cada ciclo, e pulá-la deixaria de consumi-los). Cada controlador só
/// passa a ser pulado depois de UMA coleta bem-sucedida desde o boot (backlog drenado).
/// </summary>
public sealed class OfflineRecordCollectorBackgroundService : BackgroundService
{
    private readonly IDeviceGateway _gateway;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly OfflineCollectionOptions _options;
    private readonly TimeSpan _pushIdleThreshold;
    private readonly ILogger<OfflineRecordCollectorBackgroundService> _logger;

    /// <summary>
    /// Controladores (por Id) que já tiveram UMA coleta bem-sucedida desde o boot. Um flag
    /// global de "primeira varredura feita" não serve: se a rede estava fora no boot e TODOS
    /// falharam, o backlog nunca teria sido drenado e o SkipWhenPushHealthy pularia os
    /// aparelhos assim que o push voltasse.
    /// </summary>
    private readonly HashSet<Guid> _collectedOnceSinceBoot = new();

    public OfflineRecordCollectorBackgroundService(
        IDeviceGateway gateway, IServiceScopeFactory scopeFactory,
        IOptions<OfflineCollectionOptions> options, IOptions<MonitoringOptions> monitoringOptions,
        ILogger<OfflineRecordCollectorBackgroundService> logger)
    {
        _gateway = gateway;
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _pushIdleThreshold = TimeSpan.FromMinutes(monitoringOptions.Value.PushIdleThresholdMinutes);
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Math.Max: config 0/negativa não pode derrubar o host (PeriodicTimer exige período > 0).
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(Math.Max(1, _options.IntervalMinutes)));
        do
        {
            try
            {
                await CollectAllAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha na coleta offline de registros.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task CollectAllAsync(CancellationToken ct)
    {
        List<Domain.Entities.Controller> controllers;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AccessDbContext>();
            controllers = await db.Controllers.AsNoTracking().ToListAsync(ct);
        }

        foreach (var controller in controllers)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                if (ShouldSkip(controller)) continue;

                var events = await _gateway.CollectAccessRecordsAsync(controller, ct);
                _collectedOnceSinceBoot.Add(controller.Id); // backlog deste aparelho drenado 1× desde o boot
                if (events.Count == 0) continue;

                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AccessDbContext>();
                await PersistAsync(db, controller, events, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Desligamento não é falha de coleta: para o laço em silêncio (o backlog fica no
                // aparelho e é drenado no próximo boot).
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Falha ao coletar registros do controlador {Controller} ({Ip}).",
                    controller.Name, controller.IpAddress);
            }
        }
    }

    private bool ShouldSkip(Domain.Entities.Controller controller)
    {
        // Só pula quem já teve UMA coleta bem-sucedida desde o boot (backlog drenado) E está com
        // push de EVENTO recente (keep-alive não conta: prova presença, não entrega de eventos).
        if (!_options.SkipWhenPushHealthy || !_collectedOnceSinceBoot.Contains(controller.Id)) return false;
        var lastEventPush = _gateway.GetLastEventPushUtc(controller.SerialNumber);
        return lastEventPush is not null && DateTime.UtcNow - lastEventPush < _pushIdleThreshold;
    }

    private static async Task PersistAsync(
        AccessDbContext db, Domain.Entities.Controller controller, IReadOnlyList<DeviceAccessEvent> events, CancellationToken ct)
    {
        // Serial dos registros já presentes, para deduplicar em lote.
        var incomingSerials = events.Where(e => e.RecordSerialNumber is not null)
            .Select(e => (long)e.RecordSerialNumber!.Value).ToHashSet();
        var existing = await db.AccessLogs
            .Where(l => l.ControllerSerialNumber == controller.SerialNumber
                        && l.RecordSerialNumber != null && incomingSerials.Contains(l.RecordSerialNumber.Value))
            .Select(l => l.RecordSerialNumber!.Value)
            .ToListAsync(ct);
        var known = existing.ToHashSet();

        // Nomes de usuário conhecidos (snapshot) para os códigos coletados.
        var codes = events.Where(e => e.UserCode is not null).Select(e => e.UserCode!.Value).ToHashSet();
        var names = await db.Users
            .Where(u => codes.Contains(u.UserCode))
            .Select(u => new { u.UserCode, u.Name })
            .ToDictionaryAsync(u => u.UserCode, u => u.Name, ct);

        var added = 0;
        foreach (var e in events)
        {
            if (e.RecordSerialNumber is { } serial && known.Contains(serial)) continue;
            if (e.RecordSerialNumber is { } s2 && !known.Add(s2)) continue; // dedup dentro do próprio lote

            db.AccessLogs.Add(new AccessLog
            {
                TimestampUtc = e.TimestampUtc,
                UserCode = e.UserCode,
                UserName = e.UserCode is { } uc && names.TryGetValue(uc, out var n) ? n : null,
                ControllerId = controller.Id,
                ControllerName = controller.Name,
                ControllerSerialNumber = controller.SerialNumber,
                RecordSerialNumber = e.RecordSerialNumber,
                Method = e.Method,
                RawEventCode = e.RawEventCode,
                Direction = e.Direction,
                Granted = e.Granted,
            });
            added++;
        }

        if (added > 0) await db.SaveChangesAsync(ct);
    }
}
