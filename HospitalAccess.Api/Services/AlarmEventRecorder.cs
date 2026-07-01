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
/// e grava no AlarmEvent. Mesmo padrão do AccessEventRecorder — append-only.
/// </summary>
public sealed class AlarmEventRecorder : IHostedService
{
    private readonly IDeviceGateway _gateway;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AlarmEventRecorder> _logger;

    public AlarmEventRecorder(IDeviceGateway gateway, IServiceScopeFactory scopeFactory, ILogger<AlarmEventRecorder> logger)
    {
        _gateway = gateway;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _gateway.AlarmEventReceived += OnAlarmEventReceived;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _gateway.AlarmEventReceived -= OnAlarmEventReceived;
        return Task.CompletedTask;
    }

    private void OnAlarmEventReceived(object? sender, DeviceAlarmEvent e) =>
        _ = HandleAsync(e);

    private async Task HandleAsync(DeviceAlarmEvent e)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AccessDbContext>();

            var controller = await db.Controllers.FirstOrDefaultAsync(c => c.SerialNumber == e.ControllerSerialNumber);

            db.AlarmEvents.Add(new AlarmEvent
            {
                TimestampUtc = e.TimestampUtc,
                ControllerId = controller?.Id ?? Guid.Empty,
                ControllerName = controller?.Name ?? e.ControllerSerialNumber,
                Kind = e.Kind,
                RawEventCode = e.RawEventCode,
                Cleared = e.Cleared,
            });

            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao gravar AlarmEvent para evento de {ControllerSerialNumber}.", e.ControllerSerialNumber);
        }
    }
}
