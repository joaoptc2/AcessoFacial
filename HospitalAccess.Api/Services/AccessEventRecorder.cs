using System.Threading.Channels;
using HospitalAccess.Domain.Entities;
using HospitalAccess.Gateway;
using HospitalAccess.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace HospitalAccess.Api.Services;

/// <summary>
/// Assina os eventos de acesso em tempo real do gateway (empurrados pelos controladores)
/// e grava no AccessLog. AccessLog é append-only: este é o único lugar do sistema que
/// deve chamar Add nele.
///
/// O handler do push apenas enfileira num Channel LIMITADO; um único consumidor grava em
/// micro-lotes (1 escopo/SaveChanges por lote). Antes era um fire-and-forget por evento —
/// numa rajada de push (ex.: backlog descarregando), escopos DI + DbContexts paralelos sem
/// limite disputavam o mesmo thread-pool que o SDK usa para I/O. Fila cheia = evento descartado
/// com log (a coleta offline o recupera do aparelho — é o papel dela).
/// </summary>
public sealed class AccessEventRecorder : BackgroundService
{
    private const int QueueCapacity = 2048;
    private const int MaxBatchSize = 50;

    private readonly IDeviceGateway _gateway;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AccessEventRecorder> _logger;
    private readonly Channel<DeviceAccessEvent> _queue = Channel.CreateBounded<DeviceAccessEvent>(
        new BoundedChannelOptions(QueueCapacity) { SingleReader = true });

    public AccessEventRecorder(IDeviceGateway gateway, IServiceScopeFactory scopeFactory, ILogger<AccessEventRecorder> logger)
    {
        _gateway = gateway;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _gateway.AccessEventReceived += OnAccessEventReceived;
        try
        {
            while (await _queue.Reader.WaitToReadAsync(stoppingToken))
            {
                var batch = ReadBatch();
                if (batch.Count == 0) continue;

                try
                {
                    await PersistBatchAsync(batch, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // Nenhum comando/evento pode ser perdido em silêncio: logar sempre.
                    _logger.LogError(ex, "Falha ao gravar lote de {Count} evento(s) de acesso.", batch.Count);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // desligamento normal — drenagem no finally
        }
        finally
        {
            _gateway.AccessEventReceived -= OnAccessEventReceived;
            // Drenagem best-effort no desligamento: sem isto, o que estava na fila (até a
            // capacidade inteira) era descartado em silêncio a cada restart do serviço.
            await DrainOnShutdownAsync();
        }
    }

    private List<DeviceAccessEvent> ReadBatch()
    {
        var batch = new List<DeviceAccessEvent>(MaxBatchSize);
        while (batch.Count < MaxBatchSize && _queue.Reader.TryRead(out var e))
            batch.Add(e);
        return batch;
    }

    private async Task DrainOnShutdownAsync()
    {
        try
        {
            while (true)
            {
                var batch = ReadBatch();
                if (batch.Count == 0) break;
                await PersistBatchAsync(batch, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao drenar eventos de acesso pendentes no desligamento.");
        }
    }

    private void OnAccessEventReceived(object? sender, DeviceAccessEvent e)
    {
        if (!_queue.Writer.TryWrite(e))
            _logger.LogError(
                "Fila de eventos de acesso cheia ({Capacity}); evento de {ControllerSerialNumber} descartado — a coleta offline o recupera.",
                QueueCapacity, e.ControllerSerialNumber);
    }

    private async Task PersistBatchAsync(List<DeviceAccessEvent> batch, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AccessDbContext>();

        try
        {
            await InsertNewEventsAsync(db, batch, ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Corrida com a coleta offline ENTRE a query de dedup e o SaveChanges: o mesmo
            // (SN, nº de série) foi gravado pelo outro caminho primeiro e o índice único
            // rejeitou. Sem este retry, a colisão de UM registro derrubava o lote inteiro
            // (até 50 eventos). Re-consulta a dedup e regrava só o que ainda falta.
            db.ChangeTracker.Clear();
            await InsertNewEventsAsync(db, batch, ct);
        }
    }

    private static async Task InsertNewEventsAsync(AccessDbContext db, List<DeviceAccessEvent> batch, CancellationToken ct)
    {
        var serialNumbers = batch.Select(e => e.ControllerSerialNumber).Distinct().ToList();
        var controllers = await db.Controllers
            .Where(c => serialNumbers.Contains(c.SerialNumber))
            .Select(c => new { c.SerialNumber, c.Id, c.Name })
            .ToDictionaryAsync(c => c.SerialNumber, ct);

        // Deduplicação: um mesmo registro do controlador pode chegar pelo push E pela coleta
        // offline. Uma query única cobre o lote inteiro (antes: 2-3 queries POR evento).
        var recordSerials = batch.Where(e => e.RecordSerialNumber is not null)
            .Select(e => (long)e.RecordSerialNumber!.Value).Distinct().ToList();
        var known = new HashSet<(string, long)>();
        if (recordSerials.Count > 0)
        {
            var existing = await db.AccessLogs
                .Where(l => l.ControllerSerialNumber != null && serialNumbers.Contains(l.ControllerSerialNumber)
                            && l.RecordSerialNumber != null && recordSerials.Contains(l.RecordSerialNumber.Value))
                .Select(l => new { l.ControllerSerialNumber, l.RecordSerialNumber })
                .ToListAsync(ct);
            foreach (var row in existing)
                known.Add((row.ControllerSerialNumber!, row.RecordSerialNumber!.Value));
        }

        var codes = batch.Where(e => e.UserCode is not null).Select(e => e.UserCode!.Value).Distinct().ToList();
        var names = codes.Count == 0
            ? new Dictionary<uint, string>()
            : await db.Users.Where(u => codes.Contains(u.UserCode))
                .Select(u => new { u.UserCode, u.Name })
                .ToDictionaryAsync(u => u.UserCode, u => u.Name, ct);

        var added = 0;
        foreach (var e in batch)
        {
            if (e.RecordSerialNumber is { } serial && !known.Add((e.ControllerSerialNumber, serial)))
                continue; // já gravado (banco) ou repetido dentro do próprio lote

            controllers.TryGetValue(e.ControllerSerialNumber, out var controller);
            db.AccessLogs.Add(new AccessLog
            {
                TimestampUtc = e.TimestampUtc,
                UserCode = e.UserCode,
                UserName = e.UserCode is { } uc && names.TryGetValue(uc, out var n) ? n : null,
                ControllerId = controller?.Id ?? Guid.Empty,
                ControllerName = controller?.Name ?? e.ControllerSerialNumber,
                ControllerSerialNumber = e.ControllerSerialNumber,
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
