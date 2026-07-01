using HospitalAccess.Domain.Entities;
using HospitalAccess.Gateway;
using HospitalAccess.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HospitalAccess.Api.Services;

/// <summary>
/// Assina os eventos de acesso em tempo real do gateway (empurrados pelos controladores)
/// e grava no AccessLog. AccessLog é append-only: este é o único lugar do sistema que
/// deve chamar Add nele.
/// </summary>
public sealed class AccessEventRecorder : IHostedService
{
    private readonly IDeviceGateway _gateway;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AccessEventRecorder> _logger;

    public AccessEventRecorder(IDeviceGateway gateway, IServiceScopeFactory scopeFactory, ILogger<AccessEventRecorder> logger)
    {
        _gateway = gateway;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _gateway.AccessEventReceived += OnAccessEventReceived;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _gateway.AccessEventReceived -= OnAccessEventReceived;
        return Task.CompletedTask;
    }

    private void OnAccessEventReceived(object? sender, DeviceAccessEvent e) =>
        _ = HandleAsync(e);

    private async Task HandleAsync(DeviceAccessEvent e)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AccessDbContext>();

            var controller = await db.Controllers
                .FirstOrDefaultAsync(c => c.SerialNumber == e.ControllerSerialNumber);

            string? userName = null;
            if (e.UserCode is { } userCode)
            {
                userName = (await db.Users.FirstOrDefaultAsync(u => u.UserCode == userCode))?.Name;
            }

            db.AccessLogs.Add(new AccessLog
            {
                TimestampUtc = e.TimestampUtc,
                UserCode = e.UserCode,
                UserName = userName,
                ControllerId = controller?.Id ?? Guid.Empty,
                ControllerName = controller?.Name ?? e.ControllerSerialNumber,
                Method = e.Method,
                RawEventCode = e.RawEventCode,
                Direction = e.Direction,
                Granted = e.Granted,
            });

            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            // Nenhum comando/evento pode ser perdido em silêncio: logar sempre.
            _logger.LogError(ex, "Falha ao gravar AccessLog para evento de {ControllerSerialNumber}.", e.ControllerSerialNumber);
        }
    }
}
