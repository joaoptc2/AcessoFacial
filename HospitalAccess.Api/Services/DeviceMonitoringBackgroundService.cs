using HospitalAccess.Gateway;
using HospitalAccess.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HospitalAccess.Api.Services;

/// <summary>
/// Habilita e mantém o monitoramento em tempo real (BeginWatch) em todos os controladores.
/// O 8190H NÃO empurra eventos por padrão e NÃO persiste o estado de monitoramento após reboot
/// (protocolo §10): sem este serviço, AccessEventRecorder/AlarmEventRecorder nunca recebem nada.
/// Re-arma periodicamente para reativar controladores que reiniciaram ou reconectaram.
///
/// ⚠️ Não validado contra hardware real (ver README seção 2). Se o modelo de conexão do hardware
/// exigir "phone home", configure Controller.ConnectionMode = TcpServerClient.
/// </summary>
public sealed class DeviceMonitoringBackgroundService : BackgroundService
{
    private static readonly TimeSpan RearmInterval = TimeSpan.FromMinutes(5);

    private readonly IDeviceGateway _gateway;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DeviceMonitoringBackgroundService> _logger;

    public DeviceMonitoringBackgroundService(
        IDeviceGateway gateway, IServiceScopeFactory scopeFactory, ILogger<DeviceMonitoringBackgroundService> logger)
    {
        _gateway = gateway;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Primeira ativação imediata na subida do serviço, depois re-arma periodicamente.
        await ArmAllAsync(stoppingToken);

        using var timer = new PeriodicTimer(RearmInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await ArmAllAsync(stoppingToken);
        }
    }

    private async Task ArmAllAsync(CancellationToken ct)
    {
        List<Domain.Entities.Controller> controllers;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AccessDbContext>();
            controllers = await db.Controllers.AsNoTracking().ToListAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao carregar controladores para ativar o monitoramento.");
            return;
        }

        foreach (var controller in controllers)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                await _gateway.StartMonitoringAsync(controller, ct);
            }
            catch (Exception ex)
            {
                // Controlador inacessível não pode derrubar o serviço; será re-tentado no próximo ciclo.
                _logger.LogWarning(ex, "Não foi possível ativar o monitoramento no controlador {Controller} ({Ip}).",
                    controller.Name, controller.IpAddress);
            }
        }
    }
}
