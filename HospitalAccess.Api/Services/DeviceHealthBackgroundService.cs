using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
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
/// Heartbeat de PRESENÇA dos controladores. Ordem de verificação, da mais barata para a mais cara:
/// 1. Push recente do próprio aparelho (evento/keep-alive via gateway) → online SEM NENHUMA sonda.
///    Em regime permanente (monitoramento saudável) o ciclo não gera tráfego algum.
/// 2. ICMP ping (como o UniFi) — não passa pelo SDK/ConnectorAllocator.
/// 3. TCP connect, preferindo a porta do painel HTTP (ApiBaseUrl); a porta do SDK/monitoramento
///    só como ÚLTIMO recurso (HealthCheck:AllowSdkPortFallback) — cada connect+teardown nessa
///    porta abre uma meia-sessão no canal de protocolo do aparelho, o que em redes com ICMP
///    bloqueado significava 1 sonda/min por aparelho batendo na porta do push.
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
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDeviceGateway _gateway;
    private readonly HealthCheckOptions _options;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _probeTimeout;
    private readonly ILogger<DeviceHealthBackgroundService> _logger;
    private bool _sdkFallbackWarned;

    public DeviceHealthBackgroundService(IServiceScopeFactory scopeFactory, IDeviceGateway gateway,
        IOptions<HealthCheckOptions> options, ILogger<DeviceHealthBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _gateway = gateway;
        _options = options.Value;
        // Math.Max: config 0/negativa não pode derrubar o host (PeriodicTimer exige período > 0).
        _interval = TimeSpan.FromSeconds(Math.Max(1, _options.IntervalSeconds));
        _probeTimeout = TimeSpan.FromSeconds(Math.Max(1, _options.ProbeTimeoutSeconds));
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_interval);
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
        var unreachable = new System.Collections.Concurrent.ConcurrentBag<string>();
        using var gate = new SemaphoreSlim(Math.Max(1, _options.MaxParallelChecks));
        var tasks = controllers.Select(async controller =>
        {
            await gate.WaitAsync(ct);
            try
            {
                if (await CheckOneAsync(controller, ct)) Interlocked.Increment(ref reachable);
                else unreachable.Add($"{controller.Name} ({controller.IpAddress})");
            }
            finally
            {
                gate.Release();
            }
        });
        await Task.WhenAll(tasks);
        sw.Stop();

        var offline = controllers.Count - reachable;
        // Nomeia os inalcançáveis na própria linha (até 5) — sem isso o operador via a contagem
        // mas precisava abrir o painel para descobrir QUAL aparelho olhar.
        var offlineNames = string.Join(", ", unreachable.Take(5))
            + (unreachable.Count > 5 ? $" (+{unreachable.Count - 5})" : "");

        if (HealthProbeHeuristics.SuspectLocalBottleneck(offline, controllers.Count))
        {
            // Muitos caindo DE UMA VEZ = sinal de gargalo LOCAL (não dos aparelhos). Loga o estado
            // do thread pool para diagnóstico — essa é a instrumentação para "ver" o problema.
            ThreadPool.GetAvailableThreads(out var worker, out var io);
            _logger.LogWarning(
                "Health-check: {Reach}/{Total} alcançáveis em {Ms}ms (⚠ {Off} inalcançáveis DE UMA VEZ — suspeita de gargalo local). Inalcançáveis: {Names}. ThreadPool: worker livres={W}, IO livres={IO}, threads={Count}.",
                reachable, controllers.Count, sw.ElapsedMilliseconds, offline, offlineNames, worker, io, ThreadPool.ThreadCount);
        }
        else if (offline > 0)
        {
            // Caso comum: um ou poucos aparelhos realmente fora (energia/cabo/IP) — sem alarde
            // de gargalo, mas em Warning e dizendo quem é.
            _logger.LogWarning("Health-check: {Reach}/{Total} alcançáveis em {Ms}ms. Inalcançáveis: {Names}.",
                reachable, controllers.Count, sw.ElapsedMilliseconds, offlineNames);
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
            // Push recente prova presença sem custo de rede: aparelho falou conosco há menos de
            // dois ciclos (evento, keep-alive 0x22 ou teste 0xA0).
            var lastPush = _gateway.GetLastPushActivityUtc(controller.SerialNumber);
            if (lastPush is not null && DateTime.UtcNow - lastPush < _interval * 2)
            {
                reachable = true;
            }
            else
            {
                reachable = await IsReachableAsync(controller, ct);
                if (!reachable) error = $"Sem resposta de rede (ping/tcp {controller.IpAddress}).";
            }
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
    /// disponível/permitido, um TCP connect — na porta do painel HTTP quando configurada; a porta
    /// do SDK só como último recurso. Nenhum caminho passa pelo ConnectorAllocator, então um pico
    /// de sincronização não afeta a presença.
    /// </summary>
    private async Task<bool> IsReachableAsync(Domain.Entities.Controller controller, CancellationToken ct)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(controller.IpAddress, (int)_probeTimeout.TotalMilliseconds);
            if (reply.Status == IPStatus.Success) return true;
        }
        catch
        {
            // ICMP indisponível (permissão/ambiente) — cai para o TCP connect abaixo.
        }

        // Porta neutra preferida: o painel HTTP do aparelho (não interfere no canal do protocolo).
        if (TryGetHttpPort(controller.ApiBaseUrl, out var httpPort)
            && await TcpConnectAsync(controller.IpAddress, httpPort, ct))
            return true;

        if (!_options.AllowSdkPortFallback) return false;

        if (!_sdkFallbackWarned)
        {
            _sdkFallbackWarned = true;
            _logger.LogWarning(
                "Health-check caiu no TCP connect da porta do SDK ({Ip}:{Port}) — cada sonda abre/derruba uma conexão no canal de protocolo do aparelho. " +
                "Prefira liberar ICMP na rede ou configurar ApiBaseUrl nos controladores; depois, desligue HealthCheck:AllowSdkPortFallback.",
                controller.IpAddress, controller.Port);
        }
        return await TcpConnectAsync(controller.IpAddress, controller.Port, ct);
    }

    private static bool TryGetHttpPort(string apiBaseUrl, out int port)
    {
        port = 0;
        if (string.IsNullOrWhiteSpace(apiBaseUrl)) return false;
        if (!Uri.TryCreate(apiBaseUrl, UriKind.Absolute, out var uri)) return false;
        port = uri.Port; // Uri resolve o default do esquema (80/443) quando não explícito
        return port > 0;
    }

    private async Task<bool> TcpConnectAsync(string ip, int port, CancellationToken ct)
    {
        try
        {
            using var client = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_probeTimeout);
            await client.ConnectAsync(ip, port, cts.Token);
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }
}
