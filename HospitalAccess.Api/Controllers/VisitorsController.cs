using HospitalAccess.Application.Qr;
using HospitalAccess.Domain.Entities;
using HospitalAccess.Domain.Enums;
using HospitalAccess.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

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

    public VisitorsController(AccessDbContext db, QrAccessTokenService qr, IQrImageEncoder encoder)
    {
        _db = db;
        _qr = qr;
        _encoder = encoder;
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
