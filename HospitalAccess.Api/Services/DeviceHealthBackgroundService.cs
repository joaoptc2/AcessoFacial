using HospitalAccess.Gateway;
using HospitalAccess.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HospitalAccess.Api.Services;

/// <summary>
/// Heartbeat dos controladores: periodicamente lê o SN de cada um e grava LastSeenUtc (ou o erro
/// de comunicação). Alimenta o painel de status (porta online/offline). Atualiza via ExecuteUpdate
/// para não colidir com o token de concorrência do Controller nem sobrescrever edições do admin.
/// </summary>
public sealed class DeviceHealthBackgroundService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    private readonly IDeviceGateway _gateway;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DeviceHealthBackgroundService> _logger;

    public DeviceHealthBackgroundService(
        IDeviceGateway gateway, IServiceScopeFactory scopeFactory, ILogger<DeviceHealthBackgroundService> logger)
    {
        _gateway = gateway;
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
                await CheckAllAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha no health-check dos controladores.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task CheckAllAsync(CancellationToken ct)
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

            string? error = null;
            try
            {
                await _gateway.ReadSerialNumberAsync(controller, ct);
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AccessDbContext>();
            var now = DateTime.UtcNow;
            var id = controller.Id;

            if (error is null)
            {
                await db.Controllers.Where(c => c.Id == id)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(c => c.LastSeenUtc, now)
                        .SetProperty(c => c.LastReachError, (string?)null), ct);
            }
            else
            {
                await db.Controllers.Where(c => c.Id == id)
                    .ExecuteUpdateAsync(s => s.SetProperty(c => c.LastReachError, error), ct);
            }
        }
    }
}
