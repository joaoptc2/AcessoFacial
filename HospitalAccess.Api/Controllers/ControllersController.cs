using HospitalAccess.Api.Services;
using HospitalAccess.Application.Sync;
using HospitalAccess.Domain.Entities;
using HospitalAccess.Domain.Enums;
using HospitalAccess.Gateway;
using HospitalAccess.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace HospitalAccess.Api.Controllers;

// Cadastro SIMPLIFICADO: só Nome + IP + SN são obrigatórios. Senhas vazias = usar a senha
// padrão dos aparelhos (Configurações); ApiBaseUrl vazio = derivada do IP (http://IP);
// porta/timeout/modo têm defaults. Tudo ajustável depois na edição (bloco avançado).
public record CreateControllerRequest(
    string Name, string IpAddress, string SerialNumber,
    int Port = 8000,
    string? CommunicationPassword = null, bool SupportsWaitRepeatMessage = false,
    ControllerConnectionMode ConnectionMode = ControllerConnectionMode.TcpClient,
    string? ApiBaseUrl = null, string? ApiPassword = null,
    // Slug do quarto no Home Assistant (gestão de leitos). Vazio = sem integração HA.
    string? HomeAssistantRoomId = null,
    // Este controlador é um quarto/leito? Só os marcados aparecem na Gestão de Leitos.
    bool IsRoom = false);

// CommunicationPassword é opcional na edição: em branco/nulo mantém a senha atual (não é
// devolvida pelo GET, então o formulário não a tem para reenviar). ApiPassword segue a mesma
// regra. UseDefaultPasswords=true LIMPA as duas senhas próprias → o aparelho volta a usar a
// senha padrão global das Configurações.
public record UpdateControllerRequest(
    string Name, string IpAddress, int Port, string SerialNumber,
    string? CommunicationPassword, bool SupportsWaitRepeatMessage,
    int TimeoutMs, int RestartCount,
    ControllerConnectionMode ConnectionMode = ControllerConnectionMode.TcpClient,
    string? ApiBaseUrl = null, string? ApiPassword = null,
    string? HomeAssistantRoomId = null,
    bool IsRoom = false,
    bool UseDefaultPasswords = false);

/// <summary>
/// Cadastro dos 30 controladores 8190H, status de sincronização por dispositivo e
/// comando remoto de porta. Cada controlador representa fisicamente uma única porta
/// (o hardware não suporta mais de um relé por controlador) — não existe mais uma
/// entidade "Door" separada.
///
/// RBAC: a classe permite as três funções (Admin/Operator/Reception) para que a Recepção
/// possa listar as portas e acionar abrir/fechar. TODOS os endpoints de escrita/configuração
/// e as leituras sensíveis restringem explicitamente para Admin/Operator (múltiplos [Authorize]
/// combinam por AND — a Recepção só passa onde não há restrição adicional).
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = "Admin,Operator,Reception")]
public class ControllersController : ControllerBase
{
    private readonly AccessDbContext _db;
    private readonly IDeviceGateway _gateway;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SingleFlight _singleFlight;
    private readonly IUserSyncQueue _syncQueue;
    private readonly DatabaseSchemaState _schemaState;
    private readonly PersonnelAuditState _auditState;
    private readonly ILogger<ControllersController> _logger;

    public ControllersController(
        AccessDbContext db, IDeviceGateway gateway, IServiceScopeFactory scopeFactory,
        SingleFlight singleFlight, IUserSyncQueue syncQueue, DatabaseSchemaState schemaState,
        PersonnelAuditState auditState, ILogger<ControllersController> logger)
    {
        _db = db;
        _gateway = gateway;
        _scopeFactory = scopeFactory;
        _singleFlight = singleFlight;
        _syncQueue = syncQueue;
        _schemaState = schemaState;
        _auditState = auditState;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var controllers = await _db.Controllers
            .OrderBy(c => c.Name)
            .Select(c => new
            {
                c.Id, c.Name, c.IpAddress, c.Port, c.SerialNumber, c.SupportsWaitRepeatMessage,
                c.TimeoutMs, c.RestartCount, c.IsRoom,
                UserCount = c.Permissions.Count,
            })
            .ToListAsync(ct);
        return Ok(controllers);
    }

    /// <summary>
    /// Painel de status: para cada controlador, se está online (heartbeat recente), a última vez
    /// visto, pendências de sincronização e alarmes ativos recentes. Alimenta o dashboard.
    /// </summary>
    [HttpGet("status")]
    public async Task<IActionResult> Status(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var onlineThreshold = now.AddMinutes(-3); // heartbeat a cada 1 min; 3 min sem contato = offline
        var recentAlarmSince = now.AddHours(-24);

        // É o endpoint mais consultado (polling do dashboard): 3 queries agregadas no total,
        // em vez de 2 subqueries COUNT correlacionadas POR controlador na projeção.
        // PendingSync inclui as falhas; AwaitingManual destaca as em QUARENTENA (erro permanente,
        // NextRetryAtUtc null) — o retry automático não vai resolvê-las, e o banner precisa
        // dizer isso em vez de parecer que o sistema "não sincroniza".
        var pendingByController = await _db.SyncStatuses.AsNoTracking()
            .Where(s => s.State == Domain.Enums.SyncState.Pending || s.State == Domain.Enums.SyncState.Failed)
            .GroupBy(s => s.ControllerId)
            .Select(g => new
            {
                ControllerId = g.Key,
                Count = g.Count(),
                AwaitingManual = g.Count(s => s.State == Domain.Enums.SyncState.Failed && s.NextRetryAtUtc == null),
            })
            .ToDictionaryAsync(g => g.ControllerId, g => new { g.Count, g.AwaitingManual }, ct);
        var alarmsByController = await _db.AlarmEvents.AsNoTracking()
            .Where(a => !a.Cleared && a.TimestampUtc >= recentAlarmSince)
            .GroupBy(a => a.ControllerId)
            .Select(g => new { ControllerId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.ControllerId, g => g.Count, ct);

        var rows = await _db.Controllers.AsNoTracking()
            .OrderBy(c => c.Name)
            .Select(c => new { c.Id, c.Name, c.IpAddress, c.LastSeenUtc, c.LastReachError })
            .ToListAsync(ct);

        var controllers = rows.Select(c => new
        {
            c.Id,
            c.Name,
            c.IpAddress,
            c.LastSeenUtc,
            c.LastReachError,
            Online = c.LastSeenUtc != null && c.LastSeenUtc >= onlineThreshold,
            PendingSync = pendingByController.TryGetValue(c.Id, out var p) ? p.Count : 0,
            AwaitingManualSync = pendingByController.TryGetValue(c.Id, out var m) ? m.AwaitingManual : 0,
            ActiveAlarms = alarmsByController.GetValueOrDefault(c.Id),
            // Disjuntor aberto = "rede OK, mas o protocolo não responde — comandos em espera
            // até HH:mm para proteger o aparelho". null = circuito fechado (normal).
            CircuitOpenUntilUtc = _gateway.GetCircuitOpenUntilUtc(c.Id),
        }).ToList();

        return Ok(new
        {
            generatedAtUtc = now,
            total = controllers.Count,
            online = controllers.Count(c => c.Online),
            offline = controllers.Count(c => !c.Online),
            // Snapshot do boot (custo zero por request): não-vazio = o deploy esqueceu de
            // aplicar migration — o StatusBanner mostra a faixa vermelha com a instrução.
            pendingMigrations = _schemaState.PendingMigrations,
            controllers,
        });
    }

    [HttpGet("{id:guid}")]
    [Authorize(Roles = "Admin,Operator")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var controller = await _db.Controllers.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (controller is null) return NotFound();

        // A senha de comunicação NÃO é devolvida (não expor segredo ao cliente). Na edição,
        // deixe o campo de senha em branco para mantê-la; preencha só para trocá-la.
        return Ok(new
        {
            controller.Id, controller.Name, controller.IpAddress, controller.Port, controller.SerialNumber,
            controller.SupportsWaitRepeatMessage, controller.ConnectionMode,
            controller.TimeoutMs, controller.RestartCount, controller.LastClockSyncAtUtc,
            controller.HomeAssistantRoomId,
            controller.IsRoom,
            HasCommunicationPassword = !string.IsNullOrEmpty(controller.CommunicationPassword),
            controller.ApiBaseUrl,
            HasApiPassword = !string.IsNullOrEmpty(controller.ApiPassword),
        });
    }

    /// <summary>Status de sincronização (DeviceSyncStatus) de todos os usuários neste controlador.</summary>
    [HttpGet("{id:guid}/sync-status")]
    [Authorize(Roles = "Admin,Operator")]
    public async Task<IActionResult> SyncStatus(Guid id, CancellationToken ct)
    {
        var statuses = await _db.SyncStatuses
            .Where(s => s.ControllerId == id)
            .Include(s => s.User)
            .Select(s => new
            {
                s.UserId, UserName = s.User!.Name, s.State, s.RetryCount, s.LastError, s.UpdatedAt, s.ConflictUserCode,
            })
            .ToListAsync(ct);
        return Ok(statuses);
    }

    [HttpPost]
    [Authorize(Roles = "Admin,Operator")]
    public async Task<IActionResult> Create([FromBody] CreateControllerRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest("Informe o nome do controlador.");
        if (string.IsNullOrWhiteSpace(request.IpAddress))
            return BadRequest("Informe o IP do controlador.");
        if (request.SerialNumber.Length != 16)
            return BadRequest("SerialNumber deve ter exatamente 16 dígitos.");

        var ip = request.IpAddress.Trim();
        var controller = new Domain.Entities.Controller
        {
            Name = request.Name.Trim(),
            IpAddress = ip,
            Port = request.Port,
            SerialNumber = request.SerialNumber,
            // Vazio = usar a senha padrão dos aparelhos (Configurações).
            CommunicationPassword = request.CommunicationPassword ?? string.Empty,
            SupportsWaitRepeatMessage = request.SupportsWaitRepeatMessage,
            ConnectionMode = request.ConnectionMode,
            // O painel web mora no MESMO IP do protocolo — deriva quando não informado.
            ApiBaseUrl = string.IsNullOrWhiteSpace(request.ApiBaseUrl) ? $"http://{ip}" : request.ApiBaseUrl.TrimEnd('/'),
            ApiPassword = request.ApiPassword ?? string.Empty,
            HomeAssistantRoomId = (request.HomeAssistantRoomId ?? string.Empty).Trim(),
            IsRoom = request.IsRoom,
        };

        _db.Controllers.Add(controller);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueSerialNumberViolation(ex))
        {
            return Conflict($"Já existe um controlador cadastrado com o número de série {request.SerialNumber}.");
        }

        return CreatedAtAction(nameof(Get), new { id = controller.Id }, new { controller.Id });
    }

    [HttpPut("{id:guid}")]
    [Authorize(Roles = "Admin,Operator")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateControllerRequest request, CancellationToken ct)
    {
        var controller = await _db.Controllers.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (controller is null) return NotFound();
        if (request.SerialNumber.Length != 16)
            return BadRequest("SerialNumber deve ter exatamente 16 dígitos.");
        // Desmarcar "É quarto/leito" com paciente internado esconderia um leito OCUPADO da
        // gestão (inclusive por omissão do campo num PUT antigo) — bloqueia até resolver.
        if (controller.IsRoom && !request.IsRoom &&
            await _db.BedStays.AnyAsync(s => s.ControllerId == id && s.EndedAtUtc == null, ct))
            return Conflict("Este quarto tem uma internação ativa — dê alta ou transfira o paciente antes de desmarcar 'É quarto/leito'.");

        controller.Name = request.Name;
        controller.IpAddress = request.IpAddress;
        controller.Port = request.Port;
        controller.SerialNumber = request.SerialNumber;
        // Senha em branco = manter a atual (o GET não a devolve, então a edição não a tem).
        if (!string.IsNullOrEmpty(request.CommunicationPassword))
            controller.CommunicationPassword = request.CommunicationPassword;
        controller.SupportsWaitRepeatMessage = request.SupportsWaitRepeatMessage;
        controller.ConnectionMode = request.ConnectionMode;
        controller.TimeoutMs = request.TimeoutMs;
        controller.RestartCount = request.RestartCount;
        controller.HomeAssistantRoomId = (request.HomeAssistantRoomId ?? string.Empty).Trim();
        controller.IsRoom = request.IsRoom;
        // API HTTP do painel web. Mudança de URL ou senha invalida o token cacheado (re-loga na
        // próxima chamada). ApiPassword em branco = manter a atual (não é devolvida pelo GET).
        var newApiBaseUrl = (request.ApiBaseUrl ?? string.Empty).TrimEnd('/');
        if (!string.Equals(newApiBaseUrl, controller.ApiBaseUrl, StringComparison.Ordinal))
        {
            controller.ApiBaseUrl = newApiBaseUrl;
            controller.ApiToken = null;
        }
        if (!string.IsNullOrEmpty(request.ApiPassword))
        {
            controller.ApiPassword = request.ApiPassword;
            controller.ApiToken = null;
        }
        // Voltar para a senha padrão global: limpa as senhas próprias deste aparelho.
        if (request.UseDefaultPasswords)
        {
            controller.CommunicationPassword = string.Empty;
            controller.ApiPassword = string.Empty;
            controller.ApiToken = null;
        }

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueSerialNumberViolation(ex))
        {
            return Conflict($"Já existe um controlador cadastrado com o número de série {request.SerialNumber}.");
        }

        return NoContent();
    }

    private static bool IsUniqueSerialNumberViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "IX_Controllers_SerialNumber" };

    /// <summary>Remove o controlador. Falha se ainda houver usuários com permissão nele (remova as permissões antes).</summary>
    [HttpDelete("{id:guid}")]
    [Authorize(Roles = "Admin,Operator")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var controller = await _db.Controllers.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (controller is null) return NotFound();

        var hasPermissions = await _db.Permissions.AnyAsync(p => p.ControllerId == id, ct);
        if (hasPermissions)
            return Conflict("Existem usuários com permissão neste controlador. Remova as permissões antes de excluir.");

        _db.Controllers.Remove(controller);
        await _db.SaveChangesAsync(ct);

        // Limpa o estado local do gateway (gate de serialização, monitoramento, conexão
        // persistente) — sem isto, o singleton acumulava estado de aparelhos excluídos.
        _gateway.ForgetController(controller);

        return NoContent();
    }

    /// <summary>Prova de conceito de conectividade: lê o SN reportado pelo controlador (ReadSN) e confere com o cadastrado.</summary>
    [HttpPost("{id:guid}/test-connection")]
    [Authorize(Roles = "Admin,Operator")]
    public async Task<IActionResult> TestConnection(Guid id, CancellationToken ct)
    {
        var controller = await _db.Controllers.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (controller is null) return NotFound();

        try
        {
            var sn = await _gateway.ReadSerialNumberAsync(controller, ct);
            return Ok(new { reportedSerialNumber = sn, matchesRegistered = sn == controller.SerialNumber });
        }
        catch (Exception ex)
        {
            return StatusCode(502, new { error = ex.Message });
        }
    }

    /// <summary>Abre a porta (pulso — volta a fechar após o tempo de liberação configurado no controlador).</summary>
    [HttpPost("{id:guid}/open")]
    [Authorize(Roles = "Admin,Operator,Reception")]
    public Task<IActionResult> Open(Guid id, CancellationToken ct) => RunDoorCommand(id, "AbrirPorta", _gateway.OpenDoorAsync, ct);

    /// <summary>Fecha a porta (encerra o modo "sempre aberto", se ativo).</summary>
    [HttpPost("{id:guid}/close")]
    [Authorize(Roles = "Admin,Operator,Reception")]
    public Task<IActionResult> Close(Guid id, CancellationToken ct) => RunDoorCommand(id, "FecharPorta", _gateway.CloseDoorAsync, ct);

    /// <summary>Mantém a porta aberta (modo "sempre aberto") até um comando de fechar ou trancar.</summary>
    [HttpPost("{id:guid}/hold-open")]
    [Authorize(Roles = "Admin,Operator")]
    public Task<IActionResult> HoldOpen(Guid id, CancellationToken ct) => RunDoorCommand(id, "ManterAberta", _gateway.HoldDoorOpenAsync, ct);

    /// <summary>Tranca a porta — bloqueia inclusive aberturas por credencial válida até destrancar.</summary>
    [HttpPost("{id:guid}/lock")]
    [Authorize(Roles = "Admin,Operator")]
    public Task<IActionResult> Lock(Guid id, CancellationToken ct) => RunDoorCommand(id, "TrancarPorta", _gateway.LockDoorAsync, ct);

    /// <summary>Destranca a porta (reverte o comando de trancar).</summary>
    [HttpPost("{id:guid}/unlock")]
    [Authorize(Roles = "Admin,Operator")]
    public Task<IActionResult> Unlock(Guid id, CancellationToken ct) => RunDoorCommand(id, "DestrancarPorta", _gateway.UnlockDoorAsync, ct);

    private async Task<IActionResult> RunDoorCommand(Guid id, string action, Func<Domain.Entities.Controller, CancellationToken, Task> command, CancellationToken ct)
    {
        var controller = await _db.Controllers.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (controller is null) return NotFound();

        try
        {
            await command(controller, ct);
            await AuditAsync(controller, action, success: true, error: null, ct);
            return NoContent();
        }
        catch (Exception ex)
        {
            await AuditAsync(controller, action, success: false, error: ex.Message, ct);
            return StatusCode(502, new { error = ex.Message });
        }
    }

    /// <summary>Registra na trilha de auditoria QUEM disparou QUAL comando em QUAL porta e o resultado (L6).</summary>
    private async Task AuditAsync(Domain.Entities.Controller controller, string action, bool success, string? error, CancellationToken ct)
    {
        _db.ControllerAuditLogs.Add(new ControllerAuditLog
        {
            ControllerId = controller.Id,
            ControllerName = controller.Name,
            Action = action,
            PerformedByUsername = User.Identity?.Name,
            Success = success,
            Error = error,
        });
        await _db.SaveChangesAsync(ct);
    }

    // --- Rede ---

    [HttpGet("{id:guid}/network")]
    [Authorize(Roles = "Admin,Operator")]
    public Task<IActionResult> GetNetwork(Guid id, CancellationToken ct) => RunReadAsync(id, _gateway.ReadNetworkSettingsAsync, ct);

    /// <summary>
    /// Grava a configuração de rede NO APARELHO (WriteTCPSetting). Valida antes (valor errado =
    /// aparelho inalcançável) e, se o IP gravado for diferente do cadastrado, atualiza TAMBÉM o
    /// cadastro (IpAddress + ApiBaseUrl derivada) — antes o cadastro ficava órfão do IP novo e o
    /// aparelho "sumia" logo após a gravação.
    /// </summary>
    [HttpPut("{id:guid}/network")]
    [Authorize(Roles = "Admin,Operator")]
    public async Task<IActionResult> PutNetwork(Guid id, [FromBody] ControllerNetworkInfo info, CancellationToken ct)
    {
        var controller = await _db.Controllers.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (controller is null) return NotFound();

        if (NetworkFieldRules.Validate(info) is { } validationError)
            return BadRequest(new { error = validationError });

        try
        {
            await _gateway.WriteNetworkSettingsAsync(controller, info, ct);
        }
        catch (Exception ex)
        {
            return StatusCode(502, new { error = ex.Message });
        }

        if (!string.Equals(info.Ip, controller.IpAddress, StringComparison.Ordinal))
        {
            var oldIp = controller.IpAddress;
            controller.IpAddress = info.Ip;
            controller.ApiBaseUrl = RelocateRules.FollowDerivedApiBaseUrl(controller.ApiBaseUrl, oldIp, info.Ip);
            await _db.SaveChangesAsync(ct);
            await AuditAsync(controller, $"GravarRede {oldIp}→{info.Ip}", success: true, error: null, ct);
            _logger.LogInformation(
                "Rede do controlador {Name} gravada com IP novo — cadastro acompanhou: {OldIp} → {NewIp}.",
                controller.Name, oldIp, info.Ip);
        }
        else
        {
            await AuditAsync(controller, "GravarRede", success: true, error: null, ct);
        }

        return NoContent();
    }

    /// <summary>
    /// Varredura por broadcast UDP para descobrir controladores na rede local. Sem udpPort
    /// explícito, varre as portas padrão (8101 de fábrica + 60000 legado) e mescla por SN —
    /// aparelho recém-tirado da caixa escuta em 8101 e não aparecia na varredura antiga.
    /// </summary>
    [HttpPost("discover")]
    [Authorize(Roles = "Admin,Operator")]
    public async Task<IActionResult> Discover([FromQuery] int? udpPort = null, [FromQuery] int scanSeconds = 4, CancellationToken ct = default)
    {
        var ports = udpPort is { } p ? new[] { p } : DeviceDiscovery.DefaultPorts;
        var found = await DeviceDiscovery.SweepAsync(_gateway, ports, TimeSpan.FromSeconds(scanSeconds), ct);
        return Ok(found);
    }

    /// <summary>
    /// RELOCALIZA o controlador pelo SN: varredura UDP na rede e, se o aparelho responder com
    /// IP diferente do cadastrado, atualiza o cadastro (IP + ApiBaseUrl derivada). Saída para o
    /// MAC aleatório dos 8190H: quando o DHCP troca o IP no reboot, o cadastro se re-encontra
    /// sem edição manual. Limite físico: broadcast não cruza VLAN — o servidor precisa estar
    /// na mesma L2 dos aparelhos.
    /// </summary>
    [HttpPost("{id:guid}/relocate")]
    [Authorize(Roles = "Admin,Operator")]
    public async Task<IActionResult> Relocate(Guid id, [FromQuery] int? udpPort = null, [FromQuery] int scanSeconds = 4, CancellationToken ct = default)
    {
        var controller = await _db.Controllers.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (controller is null) return NotFound();

        var ports = udpPort is { } p ? new[] { p } : DeviceDiscovery.DefaultPorts;
        var found = await DeviceDiscovery.SweepAsync(_gateway, ports, TimeSpan.FromSeconds(scanSeconds), ct);
        var match = RelocateRules.Match(controller.SerialNumber, controller.IpAddress, found);

        switch (match.Outcome)
        {
            case RelocateRules.Outcome.NotFound:
                return StatusCode(502, new
                {
                    error = $"O aparelho SN {controller.SerialNumber} não respondeu à varredura UDP " +
                            $"({found.Count} outro(s) responderam). O broadcast só alcança a mesma VLAN/L2 — " +
                            "se o servidor está em outra rede, confira o aparelho fisicamente ou ajuste o IP na edição.",
                });

            case RelocateRules.Outcome.SameIp:
                return Ok(new { moved = false, ipAddress = controller.IpAddress, message = "O aparelho respondeu no IP já cadastrado — nada a corrigir." });

            default:
                var oldIp = controller.IpAddress;
                var newIp = match.DiscoveredIp!;
                controller.IpAddress = newIp;
                controller.ApiBaseUrl = RelocateRules.FollowDerivedApiBaseUrl(controller.ApiBaseUrl, oldIp, newIp);
                await _db.SaveChangesAsync(ct);
                await AuditAsync(controller, $"RelocalizarIP {oldIp}→{newIp}", success: true, error: null, ct);
                _logger.LogWarning(
                    "Controlador {Name} (SN {Sn}) relocalizado por varredura: IP {OldIp} → {NewIp} (cadastro atualizado).",
                    controller.Name, controller.SerialNumber, oldIp, newIp);
                return Ok(new { moved = true, oldIp, ipAddress = newIp, message = $"IP atualizado: {oldIp} → {newIp}." });
        }
    }

    // --- Relógio ---

    [HttpGet("{id:guid}/clock")]
    [Authorize(Roles = "Admin,Operator")]
    public Task<IActionResult> GetClock(Guid id, CancellationToken ct) => RunReadAsync(id, _gateway.ReadControllerTimeAsync, ct);

    [HttpPost("{id:guid}/clock/sync")]
    [Authorize(Roles = "Admin,Operator")]
    public async Task<IActionResult> SyncClock(Guid id, CancellationToken ct)
    {
        var controller = await _db.Controllers.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (controller is null) return NotFound();

        try
        {
            await _gateway.SyncControllerTimeAsync(controller, ct);
            controller.LastClockSyncAtUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            return NoContent();
        }
        catch (Exception ex)
        {
            return StatusCode(502, new { error = ex.Message });
        }
    }

    // --- Alarmes ---

    [HttpGet("{id:guid}/alarm-settings")]
    [Authorize(Roles = "Admin,Operator")]
    public Task<IActionResult> GetAlarmSettings(Guid id, CancellationToken ct) => RunReadAsync(id, _gateway.ReadAlarmSettingsAsync, ct);

    [HttpPut("{id:guid}/alarm-settings")]
    [Authorize(Roles = "Admin,Operator")]
    public Task<IActionResult> PutAlarmSettings(Guid id, [FromBody] AlarmSettingsSnapshot settings, CancellationToken ct) =>
        RunWriteAsync(id, (c, ct2) => _gateway.WriteAlarmSettingsAsync(c, settings, ct2), ct);

    [HttpPost("{id:guid}/alarm-clear")]
    [Authorize(Roles = "Admin,Operator")]
    public Task<IActionResult> ClearAlarm(Guid id, CancellationToken ct) => RunWriteAsync(id, _gateway.ClearAlarmAsync, ct);

    /// <summary>Dispara o alarme de incêndio NESTE controlador (auditado).</summary>
    [HttpPost("{id:guid}/alarm-fire")]
    [Authorize(Roles = "Admin,Operator")]
    public async Task<IActionResult> TriggerFireAlarm(Guid id, CancellationToken ct)
    {
        var controller = await _db.Controllers.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (controller is null) return NotFound();
        try
        {
            await _gateway.TriggerFireAlarmAsync(controller, ct);
            await AuditAsync(controller, "DispararIncêndio", success: true, error: null, ct);
            return NoContent();
        }
        catch (Exception ex)
        {
            await AuditAsync(controller, "DispararIncêndio", success: false, error: ex.Message, ct);
            return StatusCode(502, new { error = ex.Message });
        }
    }

    // --- Ajustes locais (quiosque) ---

    [HttpGet("{id:guid}/kiosk-settings")]
    [Authorize(Roles = "Admin,Operator")]
    public Task<IActionResult> GetKioskSettings(Guid id, CancellationToken ct) => RunReadAsync(id, _gateway.ReadKioskSettingsAsync, ct);

    [HttpPut("{id:guid}/kiosk-settings")]
    [Authorize(Roles = "Admin,Operator")]
    public Task<IActionResult> PutKioskSettings(Guid id, [FromBody] KioskSettingsSnapshot settings, CancellationToken ct) =>
        RunWriteAsync(id, (c, ct2) => _gateway.WriteKioskSettingsAsync(c, settings, ct2), ct);

    // --- Leitura reversa / auditoria ---

    private sealed record AuditReport(
        List<AuditMissingUser> MissingOnDevice, List<uint> ExtraOnDevice, int DeviceCount, int ExpectedCount);

    /// <summary>
    /// Núcleo da auditoria (compartilhado entre a auditoria individual e a de todos os
    /// controladores): compara os códigos lidos do aparelho com os usuários ATIVOS que têm
    /// permissão nesta porta. Revogado/expirado NÃO deve estar no aparelho: a permissão dele
    /// permanece no banco (para reativação), e contá-la aqui fazia um visitante revogado com
    /// sucesso aparecer como "Faltando no dispositivo" para sempre — e um revogado que FICOU
    /// no aparelho passava despercebido (ele aparece como "Extra", que é o estado verdadeiro).
    /// </summary>
    private static async Task<AuditReport> BuildAuditReportAsync(
        AccessDbContext db, Guid controllerId, HashSet<uint> onDevice, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var expected = await db.Permissions.AsNoTracking()
            .Where(p => p.ControllerId == controllerId
                        && p.User!.RevokedAtUtc == null
                        && (p.User.ValidUntil == null || p.User.ValidUntil > now))
            .Select(p => new { p.UserId, p.User!.UserCode, p.User.Name, p.User.Type })
            .ToListAsync(ct);

        var expectedCodes = expected.Select(e => e.UserCode).ToHashSet();
        var missing = expected
            .Where(e => !onDevice.Contains(e.UserCode))
            .GroupBy(e => e.UserCode).Select(g => g.First())
            .OrderBy(e => e.Name)
            .Select(e => new AuditMissingUser(e.UserId, e.UserCode, e.Name, e.Type.ToString()))
            .ToList();

        return new AuditReport(
            missing,
            onDevice.Except(expectedCodes).OrderBy(c => c).ToList(),
            onDevice.Count,
            expectedCodes.Count);
    }

    /// <summary>Compara os usuários efetivamente cadastrados no controlador com as permissões no banco.</summary>
    [HttpGet("{id:guid}/personnel-audit")]
    [Authorize(Roles = "Admin,Operator")]
    public async Task<IActionResult> PersonnelAudit(Guid id, CancellationToken ct)
    {
        var controller = await _db.Controllers.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (controller is null) return NotFound();

        try
        {
            var onDevice = (await _gateway.ReadRegisteredUserCodesAsync(controller, ct)).ToHashSet();
            return Ok(await BuildAuditReportAsync(_db, id, onDevice, ct));
        }
        catch (Exception ex)
        {
            return StatusCode(502, new { error = ex.Message });
        }
    }

    /// <summary>
    /// DISPARA a auditoria de usuários em TODOS os controladores e responde na hora (202).
    ///
    /// <para>
    /// Antes isto era um GET que fazia a varredura DENTRO da requisição. Como a leitura de cada
    /// aparelho tem teto de 3 minutos e a concorrência é 4, o pior caso com vários controladores
    /// lentos passa de 20 minutos — muito além do <c>proxy_read_timeout</c> do Nginx (60 s por
    /// padrão). A tela quebrava justamente quando havia aparelho com problema, que é quando a
    /// auditoria importa. Agora a varredura roda em segundo plano e a tela consulta o resultado
    /// pelo GET — mesmo padrão do resync-all.
    /// </para>
    /// </summary>
    [HttpPost("personnel-audit-all")]
    [Authorize(Roles = "Admin,Operator")]
    public IActionResult StartPersonnelAuditAll()
    {
        const string flightKey = "personnel-audit-all";
        if (!_singleFlight.TryBegin(flightKey))
            return Conflict(new { error = "Já existe uma auditoria em andamento." });

        _ = Task.Run(async () =>
        {
            // Escopo próprio: a requisição que disparou já terminou e levou o DbContext dela.
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AccessDbContext>();
            var gateway = scope.ServiceProvider.GetRequiredService<IDeviceGateway>();
            var state = scope.ServiceProvider.GetRequiredService<PersonnelAuditState>();

            try
            {
                await RunPersonnelAuditAsync(db, gateway, state, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha na auditoria de pessoal de todos os controladores.");
                state.MarkFailed(ex.Message);
            }
            finally
            {
                _singleFlight.End(flightKey);
            }
        });

        return Accepted(new { message = "Auditoria iniciada. Acompanhe pelo estado da tela." });
    }

    /// <summary>
    /// Estado e resultado da última auditoria de todos os controladores. Enquanto <c>phase</c> é
    /// <c>Running</c>, <c>result</c> traz a auditoria ANTERIOR (a tela mostra o que já sabe em vez
    /// de piscar vazia) e <c>done</c>/<c>total</c> dão o progresso.
    /// </summary>
    [HttpGet("personnel-audit-all")]
    [Authorize(Roles = "Admin,Operator")]
    public IActionResult PersonnelAuditAll() => Ok(_auditState.Snapshot());

    /// <summary>
    /// Núcleo da varredura. Falha de um aparelho não derruba os demais: o item correspondente sai
    /// com <c>error</c> preenchido.
    /// </summary>
    private static async Task RunPersonnelAuditAsync(
        AccessDbContext db, IDeviceGateway gateway, PersonnelAuditState state, CancellationToken ct)
    {
        var controllers = await db.Controllers.AsNoTracking().OrderBy(c => c.Name).ToListAsync(ct);
        state.MarkRunning(controllers.Count);

        // Só as LEITURAS DE DISPOSITIVO rodam em paralelo (teto de 4 — ~30 aparelhos numa rede
        // sensível a rajadas). O cálculo do "esperado" usa o DbContext, que NÃO é thread-safe,
        // então roda sequencial depois que todas as leituras terminam.
        using var gate = new SemaphoreSlim(4);
        var reads = await Task.WhenAll(controllers.Select(async controller =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var codes = (await gateway.ReadRegisteredUserCodesAsync(controller, ct)).ToHashSet();
                return (Controller: controller, Codes: (HashSet<uint>?)codes, Error: (string?)null);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return (Controller: controller, Codes: null, Error: ex.Message);
            }
            finally
            {
                gate.Release();
                state.ReportProgress();
            }
        }));

        var results = new List<PersonnelAuditItem>(reads.Length);
        foreach (var read in reads)
        {
            if (read.Codes is null)
            {
                results.Add(new PersonnelAuditItem(
                    read.Controller.Id, read.Controller.Name, read.Error, null, null, null, null));
                continue;
            }

            var report = await BuildAuditReportAsync(db, read.Controller.Id, read.Codes, ct);
            results.Add(new PersonnelAuditItem(
                read.Controller.Id, read.Controller.Name, null,
                report.MissingOnDevice, report.ExtraOnDevice, report.DeviceCount, report.ExpectedCount));
        }

        state.MarkCompleted(new PersonnelAuditReport(DateTime.UtcNow, results));
    }

    /// <summary>
    /// REPARA a divergência detectada pela auditoria: usuários ativos com permissão nesta porta
    /// cujo status diz 'Synced' mas que estão AUSENTES no aparelho voltam a 'Pending' (com
    /// retry/quarentena zerados) e são reenfileirados na fila de sincronização. Divergência
    /// típica herdada da era do "sucesso silencioso" (escrita marcada Synced sem o aparelho ter
    /// gravado). Usuários EXTRAS no aparelho são apenas reportados — a remoção segue manual.
    /// </summary>
    [HttpPost("{id:guid}/personnel-audit/repair")]
    [Authorize(Roles = "Admin,Operator")]
    public async Task<IActionResult> RepairPersonnelAudit(Guid id, CancellationToken ct)
    {
        var controller = await _db.Controllers.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (controller is null) return NotFound();

        try
        {
            var onDevice = (await _gateway.ReadRegisteredUserCodesAsync(controller, ct)).ToHashSet();

            // Candidatos: permissão nesta porta + usuário ativo + status Synced (o banco acha
            // que está lá) + AUSENTE no aparelho. Visitante expirado fica de fora (não recadastrar).
            var now = DateTime.UtcNow;
            var candidates = await _db.SyncStatuses
                .Where(s => s.ControllerId == id
                            && s.State == SyncState.Synced
                            && s.User!.RevokedAtUtc == null
                            && (s.User.ValidUntil == null || s.User.ValidUntil > now)
                            && _db.Permissions.Any(p => p.ControllerId == id && p.UserId == s.UserId))
                .Include(s => s.User)
                .ToListAsync(ct);

            var repairedUserIds = new List<Guid>();
            foreach (var status in candidates)
            {
                if (onDevice.Contains(status.User!.UserCode)) continue; // realmente está no aparelho

                status.State = SyncState.Pending;
                status.RetryCount = 0;
                status.NextRetryAtUtc = null;
                status.LastError = null;
                status.UpdatedAt = DateTime.UtcNow;
                repairedUserIds.Add(status.UserId);
            }
            await _db.SaveChangesAsync(ct);

            var enqueued = _syncQueue.EnqueueMany(repairedUserIds.Distinct());
            await AuditAsync(controller, $"RepararAuditoria#{repairedUserIds.Count}", success: true, error: null, ct);
            _logger.LogInformation(
                "Reparo de auditoria no controlador {Controller}: {Count} usuário(s) marcados para re-envio ({Enqueued} enfileirados).",
                controller.Name, repairedUserIds.Count, enqueued);

            return Ok(new { repaired = repairedUserIds.Count, enqueued });
        }
        catch (Exception ex)
        {
            await AuditAsync(controller, "RepararAuditoria", success: false, error: ex.Message, ct);
            return StatusCode(502, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Reenvia UM usuário apontado como "Faltando no dispositivo" pela auditoria. O resync
    /// comum não serve aqui: ele só reseta linhas Failed, e a divergência típica é uma linha
    /// SYNCED com o usuário ausente do aparelho — o SyncUserAsync a pularia como "já ok".
    /// Este endpoint volta a linha desta porta para Pending (qualquer estado, exceto conflito
    /// de duplicidade, que tem fluxo próprio) e enfileira; sem linha, só enfileira (a
    /// sincronização cria o status ao enviar).
    /// </summary>
    [HttpPost("{id:guid}/personnel-audit/repair/{userId:guid}")]
    [Authorize(Roles = "Admin,Operator")]
    public async Task<IActionResult> RepairPersonnelAuditUser(Guid id, Guid userId, CancellationToken ct)
    {
        var controller = await _db.Controllers.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (controller is null) return NotFound();
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return NotFound(new { error = "Usuário não encontrado." });

        var now = DateTime.UtcNow;
        if (user.RevokedAtUtc != null || (user.ValidUntil != null && user.ValidUntil <= now))
            return Conflict(new { error = "Usuário revogado/expirado não é reenviado ao aparelho." });
        if (!await _db.Permissions.AnyAsync(p => p.ControllerId == id && p.UserId == userId, ct))
            return Conflict(new { error = "O usuário não tem permissão nesta porta." });

        var status = await _db.SyncStatuses
            .FirstOrDefaultAsync(s => s.ControllerId == id && s.UserId == userId, ct);
        if (status is not null)
        {
            if (status.ConflictUserCode != null)
                return Conflict(new { error = "Há um conflito de face duplicada nesta porta — resolva pelo fluxo de conflito (substituir/manter)." });
            status.State = SyncState.Pending;
            status.RetryCount = 0;
            status.NextRetryAtUtc = null;
            status.LastError = null;
            status.UpdatedAt = now;
            await _db.SaveChangesAsync(ct);
        }

        _syncQueue.EnqueueSync(userId);
        await AuditAsync(controller, $"ReenviarUsuário#{user.UserCode}", success: true, error: null, ct);
        return Accepted(new { message = "Reenvio enfileirado." });
    }

    /// <summary>
    /// Exclui uma pessoa específica (por código) diretamente do controlador — usado na auditoria,
    /// para remover cadastros indevidos (ExtraOnDevice), e na resolução de face duplicada. Auditado.
    /// </summary>
    [HttpDelete("{id:guid}/persons/{userCode:long}")]
    [Authorize(Roles = "Admin,Operator")]
    public async Task<IActionResult> DeletePersonFromDevice(Guid id, long userCode, CancellationToken ct)
    {
        var controller = await _db.Controllers.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (controller is null) return NotFound();
        try
        {
            await _gateway.DeletePersonAsync(controller, (uint)userCode, ct);
            await AuditAsync(controller, $"ExcluirPessoa#{userCode}", success: true, error: null, ct);
            return NoContent();
        }
        catch (Exception ex)
        {
            await AuditAsync(controller, $"ExcluirPessoa#{userCode}", success: false, error: ex.Message, ct);
            return StatusCode(502, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Resincronização FORÇADA: apaga TODAS as pessoas do controlador e reenvia os usuários do
    /// sistema com permissão nele. Roda em segundo plano (pode demorar). Auditado.
    /// </summary>
    [HttpPost("{id:guid}/resync-all")]
    [Authorize(Roles = "Admin,Operator")]
    public async Task<IActionResult> ResyncAll(Guid id, CancellationToken ct)
    {
        var controller = await _db.Controllers.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (controller is null) return NotFound();

        // Single-flight por controlador: cliques repetidos empilhavam ClearAllPersons +
        // re-upload COMPLETO concorrentes no mesmo aparelho — a operação mais cara que existe.
        var flightKey = $"resync:{id}";
        if (!_singleFlight.TryBegin(flightKey))
            return Conflict(new { error = "Já existe uma resincronização em andamento neste controlador." });

        try
        {
            await AuditAsync(controller, "ResincronizarForçado", success: true, error: null, ct);
        }
        catch
        {
            // Falha ANTES de agendar o job: liberar a chave, senão o endpoint fica preso em 409.
            _singleFlight.End(flightKey);
            throw;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var sync = scope.ServiceProvider.GetRequiredService<IUserSyncService>();
                await sync.ForceResyncControllerAsync(id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha no resync forçado do controlador {ControllerId}.", id);
            }
            finally
            {
                _singleFlight.End(flightKey);
            }
        });

        return Accepted(new { message = "Resincronização forçada iniciada." });
    }

    // --- Foto do evento ---

    [HttpPost("{id:guid}/event-photos/download")]
    [Authorize(Roles = "Admin,Operator")]
    public async Task<IActionResult> DownloadEventPhotos(Guid id, [FromQuery] int quantity, CancellationToken ct)
    {
        if (quantity is < 1 or > 500) quantity = 20;

        var controller = await _db.Controllers.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (controller is null) return NotFound();

        try
        {
            var photos = await _gateway.ReadRecentEventPhotosAsync(controller, quantity, ct);
            foreach (var p in photos)
            {
                _db.EventPhotos.Add(new EventPhoto
                {
                    ControllerId = controller.Id,
                    ControllerName = controller.Name,
                    UserCode = p.UserCode,
                    CapturedAtUtc = p.CapturedAtUtc,
                    RawEventCode = p.RawEventCode,
                    ImageJpg = p.ImageJpg,
                });
            }
            await _db.SaveChangesAsync(ct);
            return Ok(new { downloaded = photos.Count });
        }
        catch (Exception ex)
        {
            return StatusCode(502, new { error = ex.Message });
        }
    }

    [HttpGet("{id:guid}/event-photos")]
    [Authorize(Roles = "Admin,Operator")]
    public async Task<IActionResult> ListEventPhotos(Guid id, CancellationToken ct)
    {
        var photos = await _db.EventPhotos
            .Where(p => p.ControllerId == id)
            .OrderByDescending(p => p.CapturedAtUtc)
            .Select(p => new { p.Id, p.UserCode, p.CapturedAtUtc, p.RawEventCode, p.DownloadedAtUtc })
            .ToListAsync(ct);
        return Ok(photos);
    }

    [HttpGet("event-photos/{photoId:guid}/image")]
    [Authorize(Roles = "Admin,Operator")]
    public async Task<IActionResult> GetEventPhotoImage(Guid photoId, CancellationToken ct)
    {
        var photo = await _db.EventPhotos.FirstOrDefaultAsync(p => p.Id == photoId, ct);
        if (photo is null) return NotFound();
        return File(photo.ImageJpg, "image/jpeg");
    }

    private async Task<IActionResult> RunReadAsync<T>(Guid id, Func<Domain.Entities.Controller, CancellationToken, Task<T>> read, CancellationToken ct)
    {
        var controller = await _db.Controllers.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (controller is null) return NotFound();

        try
        {
            return Ok(await read(controller, ct));
        }
        catch (Exception ex)
        {
            return StatusCode(502, new { error = ex.Message });
        }
    }

    private async Task<IActionResult> RunWriteAsync(Guid id, Func<Domain.Entities.Controller, CancellationToken, Task> write, CancellationToken ct)
    {
        var controller = await _db.Controllers.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (controller is null) return NotFound();

        try
        {
            await write(controller, ct);
            return NoContent();
        }
        catch (Exception ex)
        {
            return StatusCode(502, new { error = ex.Message });
        }
    }
}
