using HospitalAccess.Api.Options;
using HospitalAccess.Gateway;
using HospitalAccess.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HospitalAccess.Api.Services;

/// <summary>
/// Habilita e mantém o monitoramento em tempo real (BeginWatch) em todos os controladores.
/// O 8190H NÃO empurra eventos por padrão e NÃO persiste o estado de monitoramento após reboot
/// (protocolo Classe I, "Online Transaction" 0x01 0x0B — "remains off by default and is not
/// saved"; o push em si é a Classe IX): sem este serviço, AccessEventRecorder/AlarmEventRecorder
/// nunca recebem nada.
///
/// O re-arme periódico é CONDICIONADO para não gerar tráfego à toa (antes: OpenForciblyConnect +
/// troca de handler + BeginWatch em TODOS os aparelhos a cada ciclo, mesmo já monitorando):
/// 1. Push recente (janela Monitoring:PushIdleThresholdMinutes) → pula sem nenhum comando.
/// 2. Sem push recente → ReadWatchState (comando leve): ativo → só garante o canal local;
///    inativo/erro → re-arme completo (BeginWatch).
///
/// ⚠️ Não validado contra hardware real (ver README seção 2). Se o modelo de conexão do hardware
/// exigir "phone home", configure Controller.ConnectionMode = TcpServerClient.
/// </summary>
public sealed class DeviceMonitoringBackgroundService : BackgroundService
{
    private readonly IDeviceGateway _gateway;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly MonitoringOptions _options;
    private readonly ILogger<DeviceMonitoringBackgroundService> _logger;

    public DeviceMonitoringBackgroundService(
        IDeviceGateway gateway, IServiceScopeFactory scopeFactory,
        IOptions<MonitoringOptions> options, ILogger<DeviceMonitoringBackgroundService> logger)
    {
        _gateway = gateway;
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Primeira ativação imediata na subida do serviço, depois re-arma periodicamente.
        await ArmAllAsync(stoppingToken);

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(_options.RearmIntervalMinutes));
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

        var pushIdleThreshold = TimeSpan.FromMinutes(_options.PushIdleThresholdMinutes);
        var skipped = 0;
        foreach (var controller in controllers)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                // 1. Push recente = canal comprovadamente vivo: nada a fazer (zero tráfego).
                var lastPush = _gateway.GetLastPushActivityUtc(controller.SerialNumber);
                if (lastPush is not null && DateTime.UtcNow - lastPush < pushIdleThreshold)
                {
                    skipped++;
                    continue;
                }

                // 2. Sem push recente: pergunta ao aparelho antes de re-armar às cegas.
                if (await IsAlreadyWatchingAsync(controller, ct))
                {
                    _gateway.EnsurePushChannel(controller); // só o lado local; sem BeginWatch
                    continue;
                }

                await _gateway.StartMonitoringAsync(controller, ct);
                _logger.LogInformation("Monitoramento (re)armado no controlador {Controller} ({Ip}).",
                    controller.Name, controller.IpAddress);
            }
            catch (Exception ex)
            {
                // Controlador inacessível não pode derrubar o serviço; será re-tentado no próximo ciclo.
                _logger.LogWarning(ex, "Não foi possível ativar o monitoramento no controlador {Controller} ({Ip}).",
                    controller.Name, controller.IpAddress);
            }
        }

        if (skipped > 0)
            _logger.LogDebug("Re-arme de monitoramento: {Skipped}/{Total} controlador(es) com push recente, pulados.",
                skipped, controllers.Count);
    }

    /// <summary>ReadWatchState com falha tratada como "não está monitorando" (força o re-arme completo).</summary>
    private async Task<bool> IsAlreadyWatchingAsync(Domain.Entities.Controller controller, CancellationToken ct)
    {
        try
        {
            return await _gateway.IsMonitoringActiveAsync(controller, ct);
        }
        catch
        {
            return false;
        }
    }
}
