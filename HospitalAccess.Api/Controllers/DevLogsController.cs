using HospitalAccess.Api.Services;
using HospitalAccess.Domain.Entities;
using HospitalAccess.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HospitalAccess.Api.Controllers;

public record SetDevModeRequest(bool Enabled);

/// <summary>
/// Logs de diagnóstico do MODO DE DESENVOLVIMENTO (tela "Logs (Dev)", só Admin). O modo é um
/// toggle persistido em SystemSettings e cacheado no <see cref="DevLogBuffer"/>; enquanto ativo,
/// os logs importantes do processo são espelhados num ring buffer em memória (últimas
/// <see cref="DevLogBuffer.Capacity"/> linhas). Desligado por padrão — custo zero em produção.
/// </summary>
[ApiController]
[Route("api/devlogs")]
[Authorize(Roles = "Admin")]
public class DevLogsController : ControllerBase
{
    private readonly DevLogBuffer _buffer;
    private readonly AccessDbContext _db;
    private readonly ILogger<DevLogsController> _logger;

    public DevLogsController(DevLogBuffer buffer, AccessDbContext db, ILogger<DevLogsController> logger)
    {
        _buffer = buffer;
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Lista as linhas capturadas. <paramref name="sinceId"/> permite busca incremental (a tela
    /// pede só o que ainda não tem); <paramref name="take"/> limita o lote.
    /// </summary>
    [HttpGet]
    public IActionResult List([FromQuery] long sinceId = 0, [FromQuery] int take = 500)
    {
        take = Math.Clamp(take, 1, DevLogBuffer.Capacity);
        var entries = _buffer.Snapshot(sinceId, take);
        return Ok(new
        {
            enabled = _buffer.Enabled,
            capacity = DevLogBuffer.Capacity,
            count = _buffer.Count,
            entries,
        });
    }

    /// <summary>Ativa/desativa o modo de desenvolvimento (persistido; sobrevive a restart).</summary>
    [HttpPost("mode")]
    public async Task<IActionResult> SetMode([FromBody] SetDevModeRequest request, CancellationToken ct)
    {
        // Por chave (linha única) — evita o warning de EF "First without OrderBy".
        var settings = await _db.SystemSettings
            .FirstOrDefaultAsync(s => s.Id == SystemSettings.SingletonId, ct);
        if (settings is null)
        {
            settings = new SystemSettings { Id = SystemSettings.SingletonId };
            _db.SystemSettings.Add(settings);
        }
        settings.DevelopmentModeEnabled = request.Enabled;
        settings.UpdatedAtUtc = DateTime.UtcNow;
        settings.UpdatedByUsername = User.Identity?.Name;
        await _db.SaveChangesAsync(ct);

        _buffer.SetEnabled(request.Enabled);
        // Marca a transição no próprio buffer (visível quando ativando) e no log normal do serviço.
        _logger.LogInformation("Modo de desenvolvimento {State} por {User}.",
            request.Enabled ? "ATIVADO" : "desativado", User.Identity?.Name ?? "?");

        return Ok(new { enabled = _buffer.Enabled });
    }

    /// <summary>Limpa o buffer de logs capturados (não afeta o log normal do serviço).</summary>
    [HttpDelete]
    public IActionResult Clear()
    {
        _buffer.Clear();
        _logger.LogInformation("Logs de desenvolvimento limpos por {User}.", User.Identity?.Name ?? "?");
        return NoContent();
    }
}
