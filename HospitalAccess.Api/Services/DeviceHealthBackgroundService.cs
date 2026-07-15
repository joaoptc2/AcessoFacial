using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using HospitalAccess.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HospitalAccess.Api.Services;

/// <summary>
/// Heartbeat de PRESENÇA dos controladores. A verificação é uma SONDA DE REDE leve — ICMP ping
/// (como o UniFi), com fallback para um TCP connect na porta do SDK — e NÃO usa o SDK/ConnectorAllocator.
///
/// Antes, o health-check fazia um ReadSN pelo SDK, que compartilha o mesmo ConnectorAllocator
/// (singleton) com a fila de sincronização. Um pico de comandos (ex.: ao adicionar/provisionar um
/// aparelho) saturava esse recurso GLOBAL e os ReadSN de TODOS os controladores davam timeout —
/// todos apareciam "offline" mesmo estando online (o UniFi mostrava online). A sonda de rede é
/// independente desse gargalo e reflete a mesma alcançabilidade que o UniFi vê. Roda em paralelo
/// entre controladores; um aparelho lento não atrasa os outros.
/// </summary>
public sealed class DeviceHealthBackgroundService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
    private const int MaxParallelChecks = 16;
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DeviceHealthBackgroundService> _logger;

    public DeviceHealthBackgroundService(IServiceScopeFactory scopeFactory, ILogger<DeviceHealthBackgroundService> logger)
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
        if (controllers.Count == 0) return;

        var sw = Stopwatch.StartNew();
        var reachable = 0;
        using var gate = new SemaphoreSlim(MaxParallelChecks);
        var tasks = controllers.Select(async controller =>
        {
            await gate.WaitAsync(ct);
            try
            {
                if (await CheckOneAsync(controller, ct)) Interlocked.Increment(ref reachable);
            }
            finally
            {
                gate.Release();
            }
        });
        await Task.WhenAll(tasks);
        sw.Stop();

        var offline = controllers.Count - reachable;
        // Muitos falhando de uma vez = sinal de gargalo LOCAL (não dos aparelhos). Loga o estado do
        // thread pool para diagnóstico — essa é a instrumentação para "ver" o problema.
        if (offline > 0 && offline >= controllers.Count / 2)
        {
            ThreadPool.GetAvailableThreads(out var worker, out var io);
            _logger.LogWarning(
                "Health-check: {Reach}/{Total} alcançáveis em {Ms}ms (⚠ {Off} inalcançáveis DE UMA VEZ — suspeita de gargalo local). ThreadPool: worker livres={W}, IO livres={IO}, threads={Count}.",
                reachable, controllers.Count, sw.ElapsedMilliseconds, offline, worker, io, ThreadPool.ThreadCount);
        }
        else
        {
            _logger.LogInformation("Health-check: {Reach}/{Total} alcançáveis em {Ms}ms.",
                reachable, controllers.Count, sw.ElapsedMilliseconds);
        }
    }

    /// <summary>Sonda um controlador e grava LastSeenUtc/LastReachError. Devolve true se alcançável.</summary>
    private async Task<bool> CheckOneAsync(Domain.Entities.Controller controller, CancellationToken ct)
    {
        bool reachable;
        string? error = null;
        try
        {
            reachable = await IsReachableAsync(controller.IpAddress, controller.Port, ct);
            if (!reachable) error = $"Sem resposta de rede (ping/tcp {controller.IpAddress}:{controller.Port}).";
        }
        catch (Exception ex)
        {
            reachable = false;
            error = ex.Message;
        }

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AccessDbContext>();
            var id = controller.Id;
            if (reachable)
            {
                var now = DateTime.UtcNow;
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
            _logger.LogError(ex, "Falha ao gravar o status do controlador {ControllerId}.", controller.Id);
        }

        return reachable;
    }

    /// <summary>
    /// Alcançabilidade independente do SDK: ICMP ping (como o UniFi) e, se o ICMP não estiver
    /// disponível/permitido, um TCP connect na porta do SDK. Nenhum dos dois passa pelo
    /// ConnectorAllocator, então um pico de sincronização não afeta a presença.
    /// </summary>
    private static async Task<bool> IsReachableAsync(string ip, int port, CancellationToken ct)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(ip, (int)ProbeTimeout.TotalMilliseconds);
            if (reply.Status == IPStatus.Success) return true;
        }
        catch
        {
            // ICMP indisponível (permissão/ambiente) — cai para o TCP connect abaixo.
        }

        try
        {
            using var client = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(ProbeTimeout);
            await client.ConnectAsync(ip, port, cts.Token);
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }
}
