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
/// Cadastro dos 30 controladores 8190H: listar, criar, editar, excluir, ver status e testar
/// conexão. Cada controlador representa fisicamente uma única porta (o hardware não suporta mais
/// de um relé por controlador) — não existe entidade "Door" separada.
///
/// <para>
/// As demais operações sobre o mesmo recurso vivem em controllers irmãos, todos sob a rota
/// <c>api/controllers</c> e herdando de <see cref="ControllerEndpointBase"/>: portas
/// (<see cref="ControllerDoorsController"/>), rede (<see cref="ControllerNetworkController"/>),
/// relógio, alarmes, quiosque, auditoria de pessoal e fotos de evento. Eram um arquivo só, de
/// ~1000 linhas e sete responsabilidades.
/// </para>
/// </summary>
public sealed class ControllersController : ControllerEndpointBase
{
    private readonly DatabaseSchemaState _schemaState;

    public ControllersController(AccessDbContext db, IDeviceGateway gateway, DatabaseSchemaState schemaState)
        : base(db, gateway)
    {
        _schemaState = schemaState;
    }


    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var controllers = await Db.Controllers
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
        var pendingByController = await Db.SyncStatuses.AsNoTracking()
            .Where(s => s.State == Domain.Enums.SyncState.Pending || s.State == Domain.Enums.SyncState.Failed)
            .GroupBy(s => s.ControllerId)
            .Select(g => new
            {
                ControllerId = g.Key,
                Count = g.Count(),
                AwaitingManual = g.Count(s => s.State == Domain.Enums.SyncState.Failed && s.NextRetryAtUtc == null),
            })
            .ToDictionaryAsync(g => g.ControllerId, g => new { g.Count, g.AwaitingManual }, ct);
        var alarmsByController = await Db.AlarmEvents.AsNoTracking()
            .Where(a => !a.Cleared && a.TimestampUtc >= recentAlarmSince)
            .GroupBy(a => a.ControllerId)
            .Select(g => new { ControllerId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.ControllerId, g => g.Count, ct);

        var rows = await Db.Controllers.AsNoTracking()
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
            CircuitOpenUntilUtc = Gateway.GetCircuitOpenUntilUtc(c.Id),
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
        var controller = await Db.Controllers.FirstOrDefaultAsync(c => c.Id == id, ct);
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
        var statuses = await Db.SyncStatuses
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

        Db.Controllers.Add(controller);
        try
        {
            await Db.SaveChangesAsync(ct);
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
        var controller = await Db.Controllers.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (controller is null) return NotFound();
        if (request.SerialNumber.Length != 16)
            return BadRequest("SerialNumber deve ter exatamente 16 dígitos.");
        // Desmarcar "É quarto/leito" com paciente internado esconderia um leito OCUPADO da
        // gestão (inclusive por omissão do campo num PUT antigo) — bloqueia até resolver.
        if (controller.IsRoom && !request.IsRoom &&
            await Db.BedStays.AnyAsync(s => s.ControllerId == id && s.EndedAtUtc == null, ct))
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
            await Db.SaveChangesAsync(ct);
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
        var controller = await Db.Controllers.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (controller is null) return NotFound();

        var hasPermissions = await Db.Permissions.AnyAsync(p => p.ControllerId == id, ct);
        if (hasPermissions)
            return Conflict("Existem usuários com permissão neste controlador. Remova as permissões antes de excluir.");

        Db.Controllers.Remove(controller);
        await Db.SaveChangesAsync(ct);

        // Limpa o estado local do gateway (gate de serialização, monitoramento, conexão
        // persistente) — sem isto, o singleton acumulava estado de aparelhos excluídos.
        Gateway.ForgetController(controller);

        return NoContent();
    }

    /// <summary>Prova de conceito de conectividade: lê o SN reportado pelo controlador (ReadSN) e confere com o cadastrado.</summary>
    [HttpPost("{id:guid}/test-connection")]
    [Authorize(Roles = "Admin,Operator")]
    public async Task<IActionResult> TestConnection(Guid id, CancellationToken ct)
    {
        var controller = await Db.Controllers.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (controller is null) return NotFound();

        try
        {
            var sn = await Gateway.ReadSerialNumberAsync(controller, ct);
            return Ok(new { reportedSerialNumber = sn, matchesRegistered = sn == controller.SerialNumber });
        }
        catch (Exception ex)
        {
            return StatusCode(502, new { error = ex.Message });
        }
    }
}
