using HospitalAccess.Api.Services;
using HospitalAccess.Application.Qr;
using HospitalAccess.Application.Sync;
using HospitalAccess.Domain.Entities;
using HospitalAccess.Domain.Enums;
using HospitalAccess.Gateway;
using HospitalAccess.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HospitalAccess.Api.Controllers;

public record CreateVisitorRequest(string Name, DateTime ValidUntil, int TimeGroup, Guid[]? ControllerIds = null);

/// <summary>Texto do QRCode copiado da controladora, para renderizar um PNG imprimível.</summary>
public record RenderQrRequest(string Text);

/// <summary>Cadastro de visitantes temporários e geração do QR de acesso.</summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = "Admin,Operator,Reception")]
public class VisitorsController : ControllerBase
{
    private readonly AccessDbContext _db;
    private readonly QrAccessTokenService _qr;
    private readonly IQrImageEncoder _encoder;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<VisitorsController> _logger;

    public VisitorsController(
        AccessDbContext db, QrAccessTokenService qr, IQrImageEncoder encoder,
        IServiceScopeFactory scopeFactory, ILogger<VisitorsController> logger)
    {
        _db = db;
        _qr = qr;
        _encoder = encoder;
        _scopeFactory = scopeFactory;
        _logger = logger;
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

        RevokeInBackground(id);

        visitor.RevokedByUsername = User.Identity?.Name;
        visitor.RevokedAtUtc = DateTime.UtcNow;
        UserAuditLogger.Record(_db, visitor, "Revogado", User.Identity?.Name);
        await _db.SaveChangesAsync(ct);

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

        RevokeDeletedVisitorInBackground(userCode, controllerIds);

        return NoContent();
    }

    /// <summary>Remove a pessoa dos controladores informados após o cadastro já ter sido apagado do banco.</summary>
    private void RevokeDeletedVisitorInBackground(uint userCode, List<Guid> controllerIds)
    {
        if (controllerIds.Count == 0) return;
        _ = Task.Run(async () =>
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AccessDbContext>();
            var gateway = scope.ServiceProvider.GetRequiredService<IDeviceGateway>();
            foreach (var controllerId in controllerIds)
            {
                try
                {
                    var controller = await db.Controllers.FirstOrDefaultAsync(c => c.Id == controllerId);
                    if (controller is null) continue;
                    await gateway.DeletePersonAsync(controller, userCode);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Falha ao remover visitante excluído {UserCode} do controlador {ControllerId}.", userCode, controllerId);
                }
            }
        });
    }

    /// <summary>Roda a revogação fora do ciclo de requisição (mesmo motivo do UsersController: não bloquear a resposta HTTP em hardware inacessível).</summary>
    private void RevokeInBackground(Guid userId)
    {
        _ = Task.Run(async () =>
        {
            using var scope = _scopeFactory.CreateScope();
            var sync = scope.ServiceProvider.GetRequiredService<IUserSyncService>();
            try
            {
                await sync.RevokeUserAsync(userId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha ao revogar visitante {UserId} em segundo plano.", userId);
            }
        });
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

        // Sem porta o visitante não é enviado a nenhum controlador e o QR não abre nada —
        // exigir ao menos uma porta evita o estado confuso "criado mas não sincronizado".
        var controllerIds = (request.ControllerIds ?? []).Distinct().ToList();
        if (controllerIds.Count == 0)
            return BadRequest("Selecione ao menos uma porta: o visitante é cadastrado nela e o QR só abre onde ele foi enviado.");

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

        SyncInBackground(visitor.Id);

        return CreatedAtAction(nameof(GenerateQr), new { visitorId = visitor.Id }, new { visitor.Id, visitor.UserCode });
    }

    /// <summary>Cadastra o visitante como pessoa (sem face) nos controladores das portas da visita, fora do ciclo HTTP.</summary>
    private void SyncInBackground(Guid userId)
    {
        _ = Task.Run(async () =>
        {
            using var scope = _scopeFactory.CreateScope();
            var sync = scope.ServiceProvider.GetRequiredService<IUserSyncService>();
            try
            {
                await sync.SyncUserAsync(userId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha ao sincronizar visitante {UserId} em segundo plano.", userId);
            }
        });
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
        var visitor = await _db.Users.FirstOrDefaultAsync(u => u.Id == visitorId && u.Type == UserType.Visitor, ct);
        if (visitor is null) return NotFound();
        if (visitor.ValidUntil is null) return BadRequest("Visitante sem validade definida.");
        // Não emitir QR para visitante já revogado ou vencido — evita entregar credencial que
        // não deveria mais abrir a porta.
        if (visitor.RevokedAtUtc is not null) return Conflict("Visitante revogado.");
        if (visitor.ValidUntil < DateTime.UtcNow) return Conflict("Visitante com validade expirada.");

        // SEMPRE PlainText. Confirmado byte a byte contra QRs reais gerados pelo software da
        // controladora para este modelo (todos "user_id={code}_time={microssegundos}"). O formato
        // binário do Apêndice 8 foi uma hipótese equivocada — o firmware o recusa como "QR inválido".
        // A configuração SystemSettings.QrFormat é intencionalmente IGNORADA aqui para não voltar a
        // emitir o formato binário por causa de um valor legado ("Appendix8Rc4") gravado no banco.
        var png = _encoder.EncodePng(_qr.BuildAccessToken(visitor.UserCode, DateTime.UtcNow));
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
