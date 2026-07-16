using HospitalAccess.Api.Services;
using HospitalAccess.Application.Qr;
using HospitalAccess.Domain.Entities;
using HospitalAccess.Domain.Enums;
using HospitalAccess.Infrastructure.Devices;
using HospitalAccess.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HospitalAccess.Api.Controllers;

public record CreateVisitorRequest(string Name, DateTime ValidUntil, int TimeGroup, Guid[]? ControllerIds = null);

/// <summary>Texto do QRCode copiado da controladora, para renderizar um PNG imprimível.</summary>
public record RenderQrRequest(string Text);

/// <summary>Troca o quarto (controlador/porta) de um visitante temporário.</summary>
public record ChangeRoomRequest(Guid ControllerId);

/// <summary>Cadastro de visitantes temporários e geração do QR de acesso.</summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = "Admin,Operator,Reception")]
public class VisitorsController : ControllerBase
{
    private readonly AccessDbContext _db;
    private readonly QrAccessTokenService _qr;
    private readonly IQrImageEncoder _encoder;
    private readonly DeviceQrService _deviceQr;
    private readonly IUserSyncQueue _syncQueue;

    public VisitorsController(
        AccessDbContext db, QrAccessTokenService qr, IQrImageEncoder encoder,
        DeviceQrService deviceQr, IUserSyncQueue syncQueue)
    {
        _db = db;
        _qr = qr;
        _encoder = encoder;
        _deviceQr = deviceQr;
        _syncQueue = syncQueue;
    }

    /// <summary>Lista os visitantes/temporários (usuários permanentes ficam em /api/users).</summary>
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var visitors = await _db.Users
            .Where(u => u.Type == UserType.Visitor)
            .OrderByDescending(u => u.CreatedAtUtc)
            .Select(u => new
            {
                u.Id,
                u.UserCode,
                u.Name,
                u.ValidFrom,
                u.ValidUntil,
                u.TimeGroup,
                u.CreatedByUsername,
                u.CreatedAtUtc,
                u.RevokedAtUtc,
                IsExpired = u.RevokedAtUtc == null && u.ValidUntil != null && u.ValidUntil < now,
                IsRevoked = u.RevokedAtUtc != null,
                Controllers = u.Permissions.Select(p => new { p.ControllerId, ControllerName = p.Controller!.Name }),
                // Resumo de sincronização: quantas portas já receberam o visitante (Synced) vs.
                // pendentes/falhas. Se nenhuma está Synced, o QR ainda não abre nada.
                SyncSynced = u.SyncStatuses.Count(s => s.State == Domain.Enums.SyncState.Synced),
                SyncPending = u.SyncStatuses.Count(s => s.State == Domain.Enums.SyncState.Pending || s.State == Domain.Enums.SyncState.Failed),
                SyncTotal = u.Permissions.Count,
            })
            .ToListAsync(ct);

        return Ok(visitors);
    }

    /// <summary>Revoga um visitante antes do vencimento natural do QR (mantém o cadastro; pode reativar).</summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Revoke(Guid id, CancellationToken ct)
    {
        var visitor = await _db.Users.FirstOrDefaultAsync(u => u.Id == id && u.Type == UserType.Visitor, ct);
        if (visitor is null) return NotFound();

        visitor.RevokedByUsername = User.Identity?.Name;
        visitor.RevokedAtUtc = DateTime.UtcNow;
        UserAuditLogger.Record(_db, visitor, "Revogado", User.Identity?.Name);
        await _db.SaveChangesAsync(ct);

        // Depois do commit (a revogação no hardware lê RevokedAtUtc do banco) e pela fila serial —
        // o Task.Run antigo podia rodar ANTES do commit e concorria com o job de retry no mesmo
        // usuário (fila garante 1 worker por usuário).
        _syncQueue.EnqueueRevoke(id);

        return NoContent();
    }

    /// <summary>
    /// EXCLUI o visitante (e o QR): remove a pessoa dos controladores e apaga o cadastro. Ao
    /// contrário da revogação, não é reversível. O histórico de acessos já gravado permanece
    /// (guarda nome/código como snapshot).
    /// </summary>
    [HttpDelete("{id:guid}/permanent")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var visitor = await _db.Users.FirstOrDefaultAsync(u => u.Id == id && u.Type == UserType.Visitor, ct);
        if (visitor is null) return NotFound();

        // Captura o necessário para revogar no hardware antes de apagar a linha (e o
        // DeviceSyncStatus em cascata).
        var userCode = visitor.UserCode;
        var controllerIds = await _db.SyncStatuses
            .Where(s => s.UserId == id && s.State != SyncState.Revoked)
            .Select(s => s.ControllerId)
            .Union(_db.Permissions.Where(p => p.UserId == id).Select(p => p.ControllerId))
            .Distinct()
            .ToListAsync(ct);

        UserAuditLogger.Record(_db, visitor, "Excluído", User.Identity?.Name);
        _db.Users.Remove(visitor);
        await _db.SaveChangesAsync(ct);

        // Fila serial (mesmo caminho do UsersController.Delete): a revogação roda fora do ciclo
        // HTTP sem concorrência ilimitada nem corrida com o job de retry.
        _syncQueue.EnqueueRevokeDeleted(userCode, controllerIds);

        return NoContent();
    }

    /// <summary>
    /// Cria o visitante. Não é cadastrado como Person em nenhum controlador — o QR
    /// (ver QrAccessTokenService) é validado pelo controlador; não cadastra Person.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateVisitorRequest request, CancellationToken ct)
    {
        // Normaliza para UTC: um <input datetime-local> chega sem offset (Kind=Unspecified) e,
        // se comparado/gravado como está, a validação erra por horas e o Npgsql rejeita a escrita
        // em coluna timestamptz. Tratamos "sem offset" como horário do servidor.
        var validUntil = ToUtc(request.ValidUntil);
        if (validUntil <= DateTime.UtcNow)
            return BadRequest("ValidUntil deve ser no futuro.");
        if (request.TimeGroup is < 1 or > 64)
            return BadRequest("TimeGroup deve estar entre 1 e 64.");

        // Gestão de leitos: o temporário fica em UM quarto (uma porta). O QR é cunhado por aparelho,
        // então cadastrar em várias portas geraria QRs diferentes por porta — sem sentido para um
        // paciente. Fica em 1 quarto e usa-se "Trocar quarto" (PUT /room) para mudá-lo.
        var controllerIds = (request.ControllerIds ?? []).Distinct().ToList();
        if (controllerIds.Count != 1)
            return BadRequest("Selecione exatamente UM quarto (porta): o visitante temporário fica em um leito por vez. Use \"Trocar quarto\" para mudá-lo depois.");

        var nextCode = await NextUserCodeAsync(ct);

        var visitor = new User
        {
            UserCode = nextCode,
            Name = request.Name,
            Type = UserType.Visitor,
            ValidFrom = DateTime.UtcNow,
            ValidUntil = validUntil,
            TimeGroup = request.TimeGroup,
            CreatedByUsername = User.Identity?.Name,
        };

        // Portas da visita: o visitante é cadastrado como pessoa (sem face) nesses controladores,
        // com validade nativa. O leitor abre lendo o QR (que carrega o código do visitante).
        foreach (var controllerId in controllerIds)
        {
            if (!await _db.Controllers.AnyAsync(c => c.Id == controllerId, ct))
                return BadRequest($"Controlador {controllerId} não existe.");
            visitor.Permissions.Add(new AccessPermission { ControllerId = controllerId, TimeGroup = request.TimeGroup });
        }

        _db.Users.Add(visitor);
        UserAuditLogger.Record(_db, visitor, "Criado", User.Identity?.Name);
        await _db.SaveChangesAsync(ct);

        // Fila serial com dedup — um lote de criações não dispara Task.Run concorrentes no aparelho.
        _syncQueue.EnqueueSync(visitor.Id);

        return CreatedAtAction(nameof(GenerateQr), new { visitorId = visitor.Id }, new { visitor.Id, visitor.UserCode });
    }

    /// <summary>
    /// Troca o quarto (porta/controlador) do visitante temporário. Remove a pessoa do quarto antigo
    /// (invalida o QR antigo, via HTTP People/Delete + reconciliação do SDK) e sincroniza no novo,
    /// gerando um novo QR. Usado na gestão de leitos quando o paciente muda de quarto.
    /// </summary>
    [HttpPut("{id:guid}/room")]
    public async Task<IActionResult> ChangeRoom(Guid id, [FromBody] ChangeRoomRequest request, CancellationToken ct)
    {
        var visitor = await _db.Users.Include(u => u.Permissions)
            .FirstOrDefaultAsync(u => u.Id == id && u.Type == UserType.Visitor, ct);
        if (visitor is null) return NotFound();
        if (visitor.RevokedAtUtc is not null) return Conflict("Visitante revogado.");
        if (visitor.ValidUntil is not null && visitor.ValidUntil < DateTime.UtcNow)
            return Conflict("Visitante com validade expirada.");
        if (!await _db.Controllers.AnyAsync(c => c.Id == request.ControllerId, ct))
            return BadRequest("Quarto (controlador) não existe.");

        var currentIds = visitor.Permissions.Select(p => p.ControllerId).ToList();
        if (currentIds.Count == 1 && currentIds[0] == request.ControllerId)
            return Ok(new { visitor.Id, visitor.UserCode, ControllerId = request.ControllerId, Unchanged = true });

        var oldControllerIds = currentIds.Where(cid => cid != request.ControllerId).ToList();

        // Substitui a(s) permissão(ões) antiga(s) pela nova. As linhas de sincronização antigas
        // PERMANECEM de propósito: a reconciliação do SyncUserAsync só revoga no hardware a partir
        // de um status 'Synced' cuja porta saiu da lista desejada — apagá-las aqui (comportamento
        // antigo) deixava a pessoa cadastrada via SDK no quarto antigo para sempre (só o QR era
        // limpo, via HTTP, e apenas quando ApiBaseUrl estava configurado).
        var permsToRemove = visitor.Permissions.Where(p => p.ControllerId != request.ControllerId).ToList();
        _db.Permissions.RemoveRange(permsToRemove);
        if (visitor.Permissions.All(p => p.ControllerId != request.ControllerId))
            visitor.Permissions.Add(new AccessPermission { ControllerId = request.ControllerId, TimeGroup = visitor.TimeGroup });

        UserAuditLogger.Record(_db, visitor, "Quarto alterado", User.Identity?.Name);
        await _db.SaveChangesAsync(ct);

        // Fila serial: limpa o QR antigo (HTTP) e re-sincroniza (SDK revoga o antigo, cadastra o novo).
        _syncQueue.EnqueueChangeRoom(visitor.Id, visitor.UserCode, oldControllerIds);
        return Ok(new { visitor.Id, visitor.UserCode, ControllerId = request.ControllerId });
    }

    private static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    /// <summary>Próximo UserCode via sequence do PostgreSQL (atômico/monotônico). Ver UsersController.</summary>
    private async Task<uint> NextUserCodeAsync(CancellationToken ct)
    {
        var next = await _db.Database
            .SqlQueryRaw<long>($"SELECT nextval('{AccessDbContext.UserCodeSequence}') AS \"Value\"")
            .SingleAsync(ct);
        return (uint)next;
    }

    /// <summary>Histórico administrativo do visitante (criação, revogação). Sobrevive à exclusão do cadastro.</summary>
    [HttpGet("{id:guid}/audit-log")]
    public async Task<IActionResult> AuditLog(Guid id, CancellationToken ct)
    {
        var entries = await _db.UserAuditLogs
            .Where(a => a.UserId == id)
            .OrderByDescending(a => a.TimestampUtc)
            .Select(a => new { a.TimestampUtc, a.Action, a.PerformedByUsername, a.Details })
            .ToListAsync(ct);
        return Ok(entries);
    }

    /// <summary>
    /// Gera o QR de acesso (PNG) para um visitante. O QR em si não embute a validade (ver
    /// QrAccessTokenService) — ValidUntil continua exigido e controlado pelo nosso sistema
    /// (IVisitorExpirationJob revoga automaticamente), não pelo conteúdo do QR.
    /// </summary>
    [HttpPost("{visitorId:guid}/qrcode")]
    public async Task<IActionResult> GenerateQr(Guid visitorId, CancellationToken ct)
    {
        var visitor = await _db.Users.Include(u => u.Permissions)
            .FirstOrDefaultAsync(u => u.Id == visitorId && u.Type == UserType.Visitor, ct);
        if (visitor is null) return NotFound();
        if (visitor.ValidUntil is null) return BadRequest("Visitante sem validade definida.");
        // Não emitir QR para visitante já revogado ou vencido — evita entregar credencial que
        // não deveria mais abrir a porta.
        if (visitor.RevokedAtUtc is not null) return Conflict("Visitante revogado.");
        if (visitor.ValidUntil < DateTime.UtcNow) return Conflict("Visitante com validade expirada.");

        // O QR válido é o que a CONTROLADORA gera e guarda por pessoa — o firmware o valida contra
        // esse texto exato (com um timestamp em microssegundos irreproduzível). Por isso NÃO geramos:
        // lemos via API HTTP (DeviceQrService). Se a pessoa ainda não tem QRCode no aparelho, o
        // serviço provisiona via /api/People/New (o firmware cunha o QRCode) e relê.
        DeviceQrResult? deviceQr;
        try
        {
            deviceQr = await _deviceQr.GetForUserAsync(visitor, ct);
        }
        catch (DeviceHttpException ex)
        {
            return StatusCode(502,
                $"Não foi possível obter o QR da controladora: {ex.Message}. Verifique se o controlador está online e se a URL/senha do painel web (API) estão corretas — ou use \"QR da controladora\" colando o texto manualmente.");
        }

        if (deviceQr is null)
            return UnprocessableEntity(
                "Nenhum controlador deste visitante tem a API HTTP configurada (URL + senha do painel web). " +
                "Configure em Controladores, ou use \"QR da controladora\" colando o texto manualmente.");

        var png = _encoder.EncodePng(deviceQr.QrBase64);
        Response.Headers["X-Qr-Source"] = "device";
        Response.Headers["X-Qr-Payload"] = deviceQr.Payload;
        return File(png, "image/png");
    }

    /// <summary>
    /// Renderiza um PNG de QR a partir de um TEXTO arbitrário — para reimprimir, com moldura e
    /// tamanho corretos, o QRCode que a CONTROLADORA gerou para a pessoa (o campo "QRCode" que
    /// aparece na tela do controlador). O aparelho valida o QR lido contra esse texto exato guardado
    /// no cadastro da pessoa; ele NÃO aceita um QR gerado por nós com um "time" inventado. Por isso a
    /// forma confiável é colar aqui o texto que a controladora mostra e imprimir este PNG.
    /// </summary>
    [HttpPost("qrcode/render")]
    public IActionResult RenderQrFromText([FromBody] RenderQrRequest req)
    {
        if (req is null || string.IsNullOrWhiteSpace(req.Text))
            return BadRequest("Cole o texto do QRCode que aparece na controladora.");
        var text = req.Text.Trim();
        if (text.Length > 512)
            return BadRequest("Texto do QR muito longo (máx. 512 caracteres).");
        var png = _encoder.EncodePng(text);
        return File(png, "image/png");
    }
}
