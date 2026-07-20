using HospitalAccess.Api.Options;
using HospitalAccess.Api.Services;
using HospitalAccess.Domain.Entities;
using HospitalAccess.Domain.Enums;
using HospitalAccess.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HospitalAccess.Api.Controllers;

public record AdmitPatientRequest(string PatientName, DateTime? ValidUntil = null);
public record TransferPatientRequest(Guid ToControllerId);

/// <summary>
/// Gestão de leitos (1 leito = 1 controlador/porta). Internação cria automaticamente o acesso
/// do paciente (User visitante → QR/sincronização pelo fluxo validado), gera a tela de
/// boas-vindas (JPG público em /welcome/*) e dispara o Home Assistant do quarto (best-effort).
/// Transferência reusa a mecânica da troca de quarto; alta revoga o acesso. As internações
/// encerradas são o histórico de mudanças de leito.
/// </summary>
[ApiController]
[Route("api/beds")]
[Authorize(Roles = "Admin,Operator,Reception")]
public class BedsController : ControllerBase
{
    private readonly AccessDbContext _db;
    private readonly IUserSyncQueue _syncQueue;
    private readonly WelcomeImageService _welcome;
    private readonly HomeAssistantClient _ha;
    private readonly HomeAssistantOptions _haOptions;
    private readonly BedManagementOptions _options;
    private readonly ILogger<BedsController> _logger;

    public BedsController(AccessDbContext db, IUserSyncQueue syncQueue, WelcomeImageService welcome,
        HomeAssistantClient ha, IOptions<HomeAssistantOptions> haOptions,
        IOptions<BedManagementOptions> options, ILogger<BedsController> logger)
    {
        _db = db;
        _syncQueue = syncQueue;
        _welcome = welcome;
        _ha = ha;
        _haOptions = haOptions.Value;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>Painel de leitos: cada controlador com a internação ativa (se houver) e o estado do acesso.</summary>
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var activeStays = await _db.BedStays
            .Where(s => s.EndedAtUtc == null)
            .Include(s => s.VisitorUser)
            .ToListAsync(ct);
        var stayByController = activeStays.ToDictionary(s => s.ControllerId);

        // Estado de sincronização do acesso do paciente NA porta do leito (para a UI mostrar
        // se o QR já abre a porta).
        var visitorIds = activeStays.Where(s => s.VisitorUserId != null).Select(s => s.VisitorUserId!.Value).ToList();
        var syncByUserController = await _db.SyncStatuses
            .Where(st => visitorIds.Contains(st.UserId))
            .Select(st => new { st.UserId, st.ControllerId, st.State })
            .ToListAsync(ct);

        var controllers = await _db.Controllers.AsNoTracking().OrderBy(c => c.Name).ToListAsync(ct);
        var beds = controllers.Select(c =>
        {
            stayByController.TryGetValue(c.Id, out var stay);
            var syncState = stay?.VisitorUserId is { } vid
                ? syncByUserController.FirstOrDefault(s => s.UserId == vid && s.ControllerId == c.Id)?.State.ToString()
                : null;
            return new
            {
                ControllerId = c.Id,
                c.Name,
                c.IpAddress,
                c.HomeAssistantRoomId,
                Occupied = stay is not null,
                StayId = stay?.Id,
                stay?.PatientName,
                StartedAtUtc = stay?.StartedAtUtc,
                VisitorUserId = stay?.VisitorUserId,
                AccessSyncState = syncState,
            };
        });

        return Ok(beds);
    }

    /// <summary>Histórico de internações encerradas (as mudanças de leito), mais recentes primeiro.</summary>
    [HttpGet("history")]
    public async Task<IActionResult> History([FromQuery] Guid? controllerId, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var query = _db.BedStays.AsNoTracking().Where(s => s.EndedAtUtc != null);
        if (controllerId is { } cid) query = query.Where(s => s.ControllerId == cid);

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(s => s.EndedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(s => new
            {
                s.Id,
                s.ControllerId,
                ControllerName = s.Controller!.Name,
                s.PatientName,
                s.StartedAtUtc,
                s.EndedAtUtc,
                EndReason = s.EndReason.ToString(),
                s.CreatedByUsername,
                s.EndedByUsername,
            })
            .ToListAsync(ct);

        return Ok(new { total, page, pageSize, items });
    }

    /// <summary>
    /// Interna um paciente no leito: cria a internação + o acesso (User visitante com QR) e
    /// dispara boas-vindas (JPG + Home Assistant, best-effort).
    /// </summary>
    [HttpPost("{controllerId:guid}/admit")]
    public async Task<IActionResult> Admit(Guid controllerId, [FromBody] AdmitPatientRequest request, CancellationToken ct)
    {
        var patientName = (request.PatientName ?? string.Empty).Trim();
        if (patientName.Length == 0) return BadRequest("Informe o nome do paciente.");

        var controller = await _db.Controllers.FirstOrDefaultAsync(c => c.Id == controllerId, ct);
        if (controller is null) return NotFound("Leito (controlador) não existe.");

        if (await _db.BedStays.AnyAsync(s => s.ControllerId == controllerId && s.EndedAtUtc == null, ct))
            return Conflict("Este leito já está ocupado. Transfira ou dê alta ao paciente atual antes.");

        var validUntil = request.ValidUntil is { } v
            ? ToUtc(v)
            : _options.DefaultStayDurationHours > 0
                ? DateTime.UtcNow.AddHours(_options.DefaultStayDurationHours)
                : (DateTime?)null;
        if (validUntil is { } vu && vu <= DateTime.UtcNow)
            return BadRequest("A validade do acesso deve ser no futuro.");

        // Acesso do paciente: mesmo modelo do visitante (pessoa sem face + validade nativa +
        // QR cunhado pelo aparelho) — sincronização/expiração/revogação já validadas.
        var visitor = new User
        {
            UserCode = await NextUserCodeAsync(ct),
            Name = patientName,
            Type = UserType.Visitor,
            ValidFrom = DateTime.UtcNow,
            ValidUntil = validUntil,
            TimeGroup = Math.Clamp(_options.PatientTimeGroup, 1, 64),
            CreatedByUsername = CurrentUsername(),
            Notes = $"Paciente — leito {controller.Name} (gestão de leitos)",
        };
        visitor.Permissions.Add(new AccessPermission { ControllerId = controllerId, TimeGroup = visitor.TimeGroup });
        _db.Users.Add(visitor);

        var stay = new BedStay
        {
            ControllerId = controllerId,
            PatientName = patientName,
            VisitorUserId = visitor.Id,
            CreatedByUsername = CurrentUsername(),
        };
        _db.BedStays.Add(stay);

        UserAuditLogger.Record(_db, visitor, "Internado", CurrentUsername(), $"Leito: {controller.Name}");
        await _db.SaveChangesAsync(ct);

        _syncQueue.EnqueueSync(visitor.Id);
        var (welcomeUrl, haCalled) = await TriggerWelcomeAsync(controller, patientName, ct);

        return CreatedAtAction(nameof(List), null, new
        {
            stayId = stay.Id,
            visitorUserId = visitor.Id,
            visitor.UserCode,
            welcomeImageUrl = welcomeUrl,
            homeAssistantCalled = haCalled,
        });
    }

    /// <summary>
    /// Transfere o paciente para outro leito: encerra a internação atual, abre a nova, move o
    /// acesso (mesma mecânica da troca de quarto de visitante) e refaz as boas-vindas no destino.
    /// </summary>
    [HttpPost("{controllerId:guid}/transfer")]
    public async Task<IActionResult> Transfer(Guid controllerId, [FromBody] TransferPatientRequest request, CancellationToken ct)
    {
        if (request.ToControllerId == controllerId)
            return BadRequest("O leito de destino é o mesmo de origem.");

        var stay = await _db.BedStays
            .FirstOrDefaultAsync(s => s.ControllerId == controllerId && s.EndedAtUtc == null, ct);
        if (stay is null) return NotFound("Não há internação ativa neste leito.");

        var target = await _db.Controllers.FirstOrDefaultAsync(c => c.Id == request.ToControllerId, ct);
        if (target is null) return BadRequest("Leito de destino não existe.");
        if (await _db.BedStays.AnyAsync(s => s.ControllerId == target.Id && s.EndedAtUtc == null, ct))
            return Conflict("O leito de destino já está ocupado.");

        var source = await _db.Controllers.FirstOrDefaultAsync(c => c.Id == controllerId, ct);

        stay.EndedAtUtc = DateTime.UtcNow;
        stay.EndReason = BedStayEndReason.Transfer;
        stay.EndedByUsername = CurrentUsername();

        var newStay = new BedStay
        {
            ControllerId = target.Id,
            PatientName = stay.PatientName,
            VisitorUserId = stay.VisitorUserId,
            CreatedByUsername = CurrentUsername(),
        };
        _db.BedStays.Add(newStay);

        // Move o acesso: troca a permissão para o destino e deixa as linhas de sincronização
        // antigas 'Synced' — a reconciliação revoga no aparelho de origem (fluxo validado da
        // troca de quarto de visitante).
        var oldControllerIds = new List<Guid>();
        uint visitorCode = 0;
        if (stay.VisitorUserId is { } visitorId)
        {
            var visitor = await _db.Users.Include(u => u.Permissions)
                .FirstOrDefaultAsync(u => u.Id == visitorId, ct);
            if (visitor is not null && visitor.RevokedAtUtc is null)
            {
                visitorCode = visitor.UserCode;
                oldControllerIds = visitor.Permissions
                    .Where(p => p.ControllerId != target.Id)
                    .Select(p => p.ControllerId)
                    .ToList();
                var toRemove = visitor.Permissions.Where(p => p.ControllerId != target.Id).ToList();
                _db.Permissions.RemoveRange(toRemove);
                if (visitor.Permissions.All(p => p.ControllerId != target.Id))
                    visitor.Permissions.Add(new AccessPermission { ControllerId = target.Id, TimeGroup = visitor.TimeGroup });

                visitor.Notes = $"Paciente — leito {target.Name} (gestão de leitos)";
                UserAuditLogger.Record(_db, visitor, "Transferido de leito", CurrentUsername(),
                    $"{source?.Name ?? "?"} → {target.Name}");
            }
        }

        await _db.SaveChangesAsync(ct);

        if (stay.VisitorUserId is { } vid2 && visitorCode != 0)
            _syncQueue.EnqueueChangeRoom(vid2, visitorCode, oldControllerIds);

        // HA: limpa a TV do leito antigo (se configurado) e dá boas-vindas no novo.
        if (source is not null) await TriggerClearAsync(source, ct);
        var (welcomeUrl, haCalled) = await TriggerWelcomeAsync(target, stay.PatientName, ct);

        return Ok(new
        {
            stayId = newStay.Id,
            fromController = source?.Name,
            toController = target.Name,
            welcomeImageUrl = welcomeUrl,
            homeAssistantCalled = haCalled,
        });
    }

    /// <summary>Alta: encerra a internação, revoga o acesso do paciente e limpa a TV (se configurado).</summary>
    [HttpPost("{controllerId:guid}/discharge")]
    public async Task<IActionResult> Discharge(Guid controllerId, CancellationToken ct)
    {
        var stay = await _db.BedStays
            .FirstOrDefaultAsync(s => s.ControllerId == controllerId && s.EndedAtUtc == null, ct);
        if (stay is null) return NotFound("Não há internação ativa neste leito.");

        var controller = await _db.Controllers.FirstOrDefaultAsync(c => c.Id == controllerId, ct);

        stay.EndedAtUtc = DateTime.UtcNow;
        stay.EndReason = BedStayEndReason.Discharge;
        stay.EndedByUsername = CurrentUsername();

        if (stay.VisitorUserId is { } visitorId)
        {
            var visitor = await _db.Users.FirstOrDefaultAsync(u => u.Id == visitorId, ct);
            if (visitor is not null && visitor.RevokedAtUtc is null)
            {
                visitor.RevokedAtUtc = DateTime.UtcNow;
                visitor.RevokedByUsername = CurrentUsername();
                UserAuditLogger.Record(_db, visitor, "Alta (leito)", CurrentUsername(),
                    $"Leito: {controller?.Name ?? "?"}");
            }
        }

        await _db.SaveChangesAsync(ct);

        // Depois do commit (a revogação lê RevokedAtUtc do banco), pela fila serial.
        if (stay.VisitorUserId is { } vid) _syncQueue.EnqueueRevoke(vid);
        if (controller is not null) await TriggerClearAsync(controller, ct);

        return NoContent();
    }

    /// <summary>Regenera o JPG e re-dispara as boas-vindas no HA (para a TV que perdeu o evento).</summary>
    [HttpPost("{controllerId:guid}/replay-welcome")]
    public async Task<IActionResult> ReplayWelcome(Guid controllerId, CancellationToken ct)
    {
        var stay = await _db.BedStays
            .FirstOrDefaultAsync(s => s.ControllerId == controllerId && s.EndedAtUtc == null, ct);
        if (stay is null) return NotFound("Não há internação ativa neste leito.");

        var controller = await _db.Controllers.FirstOrDefaultAsync(c => c.Id == controllerId, ct);
        if (controller is null) return NotFound();

        var (welcomeUrl, haCalled) = await TriggerWelcomeAsync(controller, stay.PatientName, ct);
        return Ok(new { welcomeImageUrl = welcomeUrl, homeAssistantCalled = haCalled });
    }

    /// <summary>
    /// Gera o JPG de boas-vindas e chama o HA. BEST-EFFORT: qualquer falha loga e devolve o
    /// que conseguiu — nunca derruba a operação de leito que a disparou.
    /// </summary>
    private async Task<(string? WelcomeUrl, bool HaCalled)> TriggerWelcomeAsync(
        Domain.Entities.Controller controller, string patientName, CancellationToken ct)
    {
        string? url = null;
        if (_welcome.Configured)
        {
            try
            {
                url = await _welcome.GenerateAsync(controller.Id, patientName, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Falha ao gerar a tela de boas-vindas do leito {Controller}.", controller.Name);
            }
        }
        else
        {
            _logger.LogInformation(
                "Tela de boas-vindas não configurada (BedManagement:WelcomeBaseImagePath) — pulando geração para o leito {Controller}.",
                controller.Name);
        }

        var haCalled = false;
        if (url is not null && !string.IsNullOrWhiteSpace(controller.HomeAssistantRoomId))
        {
            haCalled = await _ha.CallServiceAsync(
                _haOptions.WelcomeService,
                HomeAssistantPayload.Welcome(controller.HomeAssistantRoomId, patientName, url), ct);
        }
        return (url, haCalled);
    }

    private async Task TriggerClearAsync(Domain.Entities.Controller controller, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_haOptions.ClearService) ||
            string.IsNullOrWhiteSpace(controller.HomeAssistantRoomId))
            return;
        await _ha.CallServiceAsync(_haOptions.ClearService,
            HomeAssistantPayload.Clear(controller.HomeAssistantRoomId), ct);
    }

    /// <summary>Mesma sequence atômica do cadastro de visitantes (nunca reusa código de excluído).</summary>
    private async Task<uint> NextUserCodeAsync(CancellationToken ct)
    {
        var next = await _db.Database
            .SqlQueryRaw<long>($"SELECT nextval('{AccessDbContext.UserCodeSequence}') AS \"Value\"")
            .SingleAsync(ct);
        return (uint)next;
    }

    private string? CurrentUsername() => User.Identity?.Name;

    /// <summary>Um &lt;input datetime-local&gt; chega sem offset (Kind=Unspecified) — trata como horário do servidor.</summary>
    private static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Local).ToUniversalTime(),
    };
}
