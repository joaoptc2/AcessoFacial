using System.Threading.Channels;
using HospitalAccess.Domain.Entities;
using HospitalAccess.Gateway;
using HospitalAccess.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HospitalAccess.Api.Services;

/// <summary>
/// Assina os eventos de alarme em tempo real do gateway (empurrados pelos controladores)
/// e grava no AlarmEvent. Mesmo padrão do <see cref="AccessEventRecorder"/> — append-only,
/// Channel limitado + consumidor único em micro-lote (sem fire-and-forget por evento).
/// </summary>
public sealed class AlarmEventRecorder : BackgroundService
{
    private const int QueueCapacity = 2048;
    private const int MaxBatchSize = 50;

    private readonly IDeviceGateway _gateway;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AlarmEventRecorder> _logger;
    private readonly Channel<DeviceAlarmEvent> _queue = Channel.CreateBounded<DeviceAlarmEvent>(
        new BoundedChannelOptions(QueueCapacity) { SingleReader = true });

    public AlarmEventRecorder(IDeviceGateway gateway, IServiceScopeFactory scopeFactory, ILogger<AlarmEventRecorder> logger)
    {
        _gateway = gateway;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _gateway.AlarmEventReceived += OnAlarmEventReceived;
        try
        {
            while (await _queue.Reader.WaitToReadAsync(stoppingToken))
            {
                var batch = new List<DeviceAlarmEvent>(MaxBatchSize);
                while (batch.Count < MaxBatchSize && _queue.Reader.TryRead(out var e))
                    batch.Add(e);
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
                    _logger.LogError(ex, "Falha ao gravar lote de {Count} evento(s) de alarme.", batch.Count);
                }
            }
        }
        finally
        {
            _gateway.AlarmEventReceived -= OnAlarmEventReceived;
        }
    }

    private void OnAlarmEventReceived(object? sender, DeviceAlarmEvent e)
    {
        if (!_queue.Writer.TryWrite(e))
            _logger.LogError("Fila de eventos de alarme cheia ({Capacity}); evento de {ControllerSerialNumber} descartado.",
                QueueCapacity, e.ControllerSerialNumber);
    }

    private async Task PersistBatchAsync(List<DeviceAlarmEvent> batch, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AccessDbContext>();

        var serialNumbers = batch.Select(e => e.ControllerSerialNumber).Distinct().ToList();
        var controllers = await db.Controllers
            .Where(c => serialNumbers.Contains(c.SerialNumber))
            .Select(c => new { c.SerialNumber, c.Id, c.Name })
            .ToDictionaryAsync(c => c.SerialNumber, ct);

        foreach (var e in batch)
        {
            controllers.TryGetValue(e.ControllerSerialNumber, out var controller);
            db.AlarmEvents.Add(new AlarmEvent
            {
                TimestampUtc = e.TimestampUtc,
                ControllerId = controller?.Id ?? Guid.Empty,
                ControllerName = controller?.Name ?? e.ControllerSerialNumber,
                Kind = e.Kind,
                RawEventCode = e.RawEventCode,
                Cleared = e.Cleared,
            });
        }

        await db.SaveChangesAsync(ct);
    }
}
