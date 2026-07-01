using HospitalAccess.Application.Qr;
using HospitalAccess.Application.Sync;
using HospitalAccess.Domain.Entities;
using HospitalAccess.Domain.Enums;
using HospitalAccess.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HospitalAccess.Api.Controllers;

public record CreateVisitorRequest(string Name, DateTime ValidUntil, int TimeGroup);

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
            })
            .ToListAsync(ct);

        return Ok(visitors);
    }

    /// <summary>Revoga um visitante antes do vencimento natural do QR.</summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Revoke(Guid id, CancellationToken ct)
    {
        var visitor = await _db.Users.FirstOrDefaultAsync(u => u.Id == id && u.Type == UserType.Visitor, ct);
        if (visitor is null) return NotFound();

        RevokeInBackground(id);

        visitor.RevokedByUsername = User.Identity?.Name;
        visitor.RevokedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        return NoContent();
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
    /// (Appendix 8) é validado offline pelo próprio dispositivo; ver VisitorCardNumber.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateVisitorRequest request, CancellationToken ct)
    {
        if (request.ValidUntil <= DateTime.UtcNow)
            return BadRequest("ValidUntil deve ser no futuro.");
        if (request.ValidUntil.Year > QrAccessTokenService.YearBase + 63)
            return BadRequest($"ValidUntil não pode ultrapassar o ano {QrAccessTokenService.YearBase + 63} (limite do tempo comprimido do protocolo).");
        if (request.TimeGroup is < 1 or > 64)
            return BadRequest("TimeGroup deve estar entre 1 e 64.");

        var nextCode = (await _db.Users.MaxAsync(u => (uint?)u.UserCode, ct) ?? 0) + 1;

        var visitor = new User
        {
            UserCode = nextCode,
            Name = request.Name,
            Type = UserType.Visitor,
            ValidFrom = DateTime.UtcNow,
            ValidUntil = request.ValidUntil,
            TimeGroup = request.TimeGroup,
            CreatedByUsername = User.Identity?.Name,
        };

        _db.Users.Add(visitor);
        await _db.SaveChangesAsync(ct);

        return CreatedAtAction(nameof(GenerateQr), new { visitorId = visitor.Id }, new { visitor.Id, visitor.UserCode });
    }

    /// <summary>Gera o QR de acesso (PNG) para um visitante com validade embutida.</summary>
    [HttpPost("{visitorId:guid}/qrcode")]
    public async Task<IActionResult> GenerateQr(Guid visitorId, CancellationToken ct)
    {
        var visitor = await _db.Users.FirstOrDefaultAsync(u => u.Id == visitorId && u.Type == UserType.Visitor, ct);
        if (visitor is null) return NotFound();
        if (visitor.ValidUntil is not { } expiration) return BadRequest("Visitante sem validade definida.");

        var card = VisitorCardNumber.FromUserCode(visitor.UserCode);
        var token = _qr.BuildEncryptedToken(card, expiration);
        var png = _encoder.EncodePng(token);
        return File(png, "image/png");
    }
}
