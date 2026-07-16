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

public record CreateControllerRequest(
    string Name, string IpAddress, int Port, string SerialNumber,
    string CommunicationPassword, bool SupportsWaitRepeatMessage,
    ControllerConnectionMode ConnectionMode = ControllerConnectionMode.TcpClient,
    // API HTTP do painel web (para ler o QRCode). ApiBaseUrl vazio = só SDK. ApiPassword vazio =
    // usar o padrão global (Device:DefaultApiPassword).
    string? ApiBaseUrl = null, string? ApiPassword = null);

// CommunicationPassword é opcional na edição: em branco/nulo mantém a senha atual (não é
// devolvida pelo GET, então o formulário não a tem para reenviar). ApiPassword segue a mesma regra.
public record UpdateControllerRequest(
    string Name, string IpAddress, int Port, string SerialNumber,
    string? CommunicationPassword, bool SupportsWaitRepeatMessage,
    int TimeoutMs, int RestartCount,
    ControllerConnectionMode ConnectionMode = ControllerConnectionMode.TcpClient,
    string? ApiBaseUrl = null, string? ApiPassword = null);

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
    private readonly ILogger<ControllersController> _logger;

    public ControllersController(
        AccessDbContext db, IDeviceGateway gateway, IServiceScopeFactory scopeFactory,
        SingleFlight singleFlight, ILogger<ControllersController> logger)
    {
        _db = db;
        _gateway = gateway;
        _scopeFactory = scopeFactory;
        _singleFlight = singleFlight;
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
                c.TimeoutMs, c.RestartCount,
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
        var pendingByController = await _db.SyncStatuses.AsNoTracking()
            .Where(s => s.State == Domain.Enums.SyncState.Pending || s.State == Domain.Enums.SyncState.Failed)
            .GroupBy(s => s.ControllerId)
            .Select(g => new { ControllerId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.ControllerId, g => g.Count, ct);
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
            PendingSync = pendingByController.GetValueOrDefault(c.Id),
            ActiveAlarms = alarmsByController.GetValueOrDefault(c.Id),
        }).ToList();

        return Ok(new
        {
            generatedAtUtc = now,
            total = controllers.Count,
            online = controllers.Count(c => c.Online),
            offline = controllers.Count(c => !c.Online),
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
        if (request.SerialNumber.Length != 16)
            return BadRequest("SerialNumber deve ter exatamente 16 dígitos.");

        var controller = new Domain.Entities.Controller
        {
            Name = request.Name,
            IpAddress = request.IpAddress,
            Port = request.Port,
            SerialNumber = request.SerialNumber,
            CommunicationPassword = request.CommunicationPassword,
            SupportsWaitRepeatMessage = request.SupportsWaitRepeatMessage,
            ConnectionMode = request.ConnectionMode,
            ApiBaseUrl = (request.ApiBaseUrl ?? string.Empty).TrimEnd('/'),
            ApiPassword = request.ApiPassword ?? string.Empty,
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

    [HttpPut("{id:guid}/network")]
    [Authorize(Roles = "Admin,Operator")]
    public Task<IActionResult> PutNetwork(Guid id, [FromBody] ControllerNetworkInfo info, CancellationToken ct) =>
        RunWriteAsync(id, (c, ct2) => _gateway.WriteNetworkSettingsAsync(c, info, ct2), ct);

    /// <summary>Varredura por broadcast UDP para descobrir controladores desconhecidos na rede local. Não validado contra hardware real.</summary>
    [HttpPost("discover")]
    [Authorize(Roles = "Admin,Operator")]
    public async Task<IActionResult> Discover([FromQuery] int udpPort = 60000, [FromQuery] int scanSeconds = 4, CancellationToken ct = default)
    {
        var found = await _gateway.DiscoverControllersAsync(udpPort, TimeSpan.FromSeconds(scanSeconds), ct);
        return Ok(found);
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
            var expected = (await _db.Permissions
                .Where(p => p.ControllerId == id)
                .Select(p => p.User!.UserCode)
                .ToListAsync(ct)).ToHashSet();

            return Ok(new
            {
                MissingOnDevice = expected.Except(onDevice).ToList(),
                ExtraOnDevice = onDevice.Except(expected).ToList(),
                DeviceCount = onDevice.Count,
                ExpectedCount = expected.Count,
            });
        }
        catch (Exception ex)
        {
            return StatusCode(502, new { error = ex.Message });
        }
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

        await AuditAsync(controller, "ResincronizarForçado", success: true, error: null, ct);

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
