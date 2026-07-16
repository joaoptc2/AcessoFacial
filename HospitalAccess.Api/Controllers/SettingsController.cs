using HospitalAccess.Domain.Entities;
using HospitalAccess.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HospitalAccess.Api.Controllers;

public record UpdateSettingsRequest(
    int EventPhotoRetentionDays,
    int AccessLogRetentionDays,
    int AlarmLogRetentionDays,
    int ControllerAuditRetentionDays,
    string QrFormat);

/// <summary>
/// Configurações globais do sistema (linha única). Hoje concentra as políticas de retenção de
/// dados (LGPD). Um prazo 0 = reter indefinidamente. Só Admin.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = "Admin")]
public class SettingsController : ControllerBase
{
    private readonly AccessDbContext _db;

    public SettingsController(AccessDbContext db)
    {
        _db = db;
    }

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var settings = await GetOrCreateAsync(ct);
        return Ok(new
        {
            settings.EventPhotoRetentionDays,
            settings.AccessLogRetentionDays,
            settings.AlarmLogRetentionDays,
            settings.ControllerAuditRetentionDays,
            settings.QrFormat,
            settings.UpdatedAtUtc,
            settings.UpdatedByUsername,
        });
    }

    [HttpPut]
    public async Task<IActionResult> Update([FromBody] UpdateSettingsRequest request, CancellationToken ct)
    {
        if (request.EventPhotoRetentionDays < 0 || request.AccessLogRetentionDays < 0
            || request.AlarmLogRetentionDays < 0 || request.ControllerAuditRetentionDays < 0)
            return BadRequest("Os prazos de retenção não podem ser negativos (0 = reter indefinidamente).");
        if (request.QrFormat is not ("Appendix8Rc4" or "PlainText"))
            return BadRequest("QrFormat deve ser 'Appendix8Rc4' ou 'PlainText'.");

        var settings = await GetOrCreateAsync(ct);
        settings.EventPhotoRetentionDays = request.EventPhotoRetentionDays;
        settings.AccessLogRetentionDays = request.AccessLogRetentionDays;
        settings.AlarmLogRetentionDays = request.AlarmLogRetentionDays;
        settings.ControllerAuditRetentionDays = request.ControllerAuditRetentionDays;
        settings.QrFormat = request.QrFormat;
        settings.UpdatedAtUtc = DateTime.UtcNow;
        settings.UpdatedByUsername = User.Identity?.Name;

        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    private async Task<SystemSettings> GetOrCreateAsync(CancellationToken ct)
    {
        // Por chave (linha única): FirstOrDefault sem filtro dispara o warning de EF
        // "First without OrderBy" a cada chamada — ruído no log espelhado do modo dev.
        var settings = await _db.SystemSettings
            .FirstOrDefaultAsync(s => s.Id == SystemSettings.SingletonId, ct);
        if (settings is null)
        {
            settings = new SystemSettings { Id = SystemSettings.SingletonId };
            _db.SystemSettings.Add(settings);
            await _db.SaveChangesAsync(ct);
        }
        return settings;
    }
}
