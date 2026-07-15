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

    // Verifica os controladores EM PARALELO: antes era em série, então um controlador lento (ex.: com
    // ping ruim, ~30s de timeout) atrasava a verificação de todos os seguintes — o LastSeenUtc dos
    // saudáveis envelhecia além do limiar de 3 min e eles apareciam "offline" mesmo online. A
    // serialização real é por-controlador (lock no gateway); aqui cada aparelho é checado no seu tempo.
    private const int MaxParallelChecks = 12;

    private async Task CheckAllAsync(CancellationToken ct)
    {
        List<Domain.Entities.Controller> controllers;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AccessDbContext>();
            controllers = await db.Controllers.AsNoTracking().ToListAsync(ct);
        }

        using var gate = new SemaphoreSlim(MaxParallelChecks);
        var tasks = controllers.Select(async controller =>
        {
            await gate.WaitAsync(ct);
            try { await CheckOneAsync(controller, ct); }
            finally { gate.Release(); }
        });
        await Task.WhenAll(tasks);
    }

    private async Task CheckOneAsync(Domain.Entities.Controller controller, CancellationToken ct)
    {
        try
        {
            string? error = null;
            try
            {
                await _gateway.ReadSerialNumberAsync(controller, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
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
        catch (Exception ex)
        {
            // Uma falha (ex.: no banco) num controlador não deve abortar a verificação dos demais.
            _logger.LogError(ex, "Falha ao verificar o controlador {ControllerId}.", controller.Id);
        }
    }
}
