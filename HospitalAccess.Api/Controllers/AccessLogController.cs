using System.Globalization;
using System.Text;
using HospitalAccess.Domain.Enums;
using HospitalAccess.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HospitalAccess.Api.Controllers;

/// <summary>Consulta e exportação do log de acessos (auditoria). AccessLog é append-only: só leitura aqui.</summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = "Admin,Operator")]
public class AccessLogController : ControllerBase
{
    private const int MaxPageSize = 500;

    private readonly AccessDbContext _db;

    public AccessLogController(AccessDbContext db)
    {
        _db = db;
    }

    /// <summary>Lista com filtros (período, usuário, controlador, método) e paginação.</summary>
    [HttpGet]
    public async Task<IActionResult> Query(
        [FromQuery] DateTime? from, [FromQuery] DateTime? to,
        [FromQuery] uint? userCode, [FromQuery] Guid? controllerId, [FromQuery] AccessMethod? method,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default)
    {
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);
        page = Math.Max(page, 1);

        var query = BuildFilteredQuery(from, to, userCode, controllerId, method);

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(l => l.TimestampUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return Ok(new { total, page, pageSize, items });
    }

    /// <summary>Exporta CSV. PDF ainda não implementado (ver README — pendente escolha de biblioteca).</summary>
    [HttpGet("export")]
    public async Task<IActionResult> Export(
        [FromQuery] DateTime? from, [FromQuery] DateTime? to,
        [FromQuery] uint? userCode, [FromQuery] Guid? controllerId, [FromQuery] AccessMethod? method,
        [FromQuery] string format = "csv", CancellationToken ct = default)
    {
        if (!string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase))
            return StatusCode(501, "Exportação em PDF ainda não implementada. Use format=csv.");

        var items = await BuildFilteredQuery(from, to, userCode, controllerId, method)
            .OrderByDescending(l => l.TimestampUtc)
            .ToListAsync(ct);

        var csv = new StringBuilder();
        csv.AppendLine("TimestampUtc,UserCode,UserName,ControllerName,DoorName,Method,RawEventCode,Direction,Granted");
        foreach (var log in items)
        {
            csv.AppendLine(string.Join(',',
                log.TimestampUtc.ToString("O", CultureInfo.InvariantCulture),
                log.UserCode?.ToString(CultureInfo.InvariantCulture) ?? "",
                CsvEscape(log.UserName),
                CsvEscape(log.ControllerName),
                CsvEscape(log.DoorName),
                log.Method.ToString(),
                log.RawEventCode.ToString(CultureInfo.InvariantCulture),
                log.Direction?.ToString(CultureInfo.InvariantCulture) ?? "",
                log.Granted.ToString()));
        }

        var bytes = Encoding.UTF8.GetBytes(csv.ToString());
        return File(bytes, "text/csv", $"access-log-{DateTime.UtcNow:yyyyMMddHHmmss}.csv");
    }

    private IQueryable<Domain.Entities.AccessLog> BuildFilteredQuery(
        DateTime? from, DateTime? to, uint? userCode, Guid? controllerId, AccessMethod? method)
    {
        var query = _db.AccessLogs.AsNoTracking().AsQueryable();
        if (from is not null) query = query.Where(l => l.TimestampUtc >= from);
        if (to is not null) query = query.Where(l => l.TimestampUtc <= to);
        if (userCode is not null) query = query.Where(l => l.UserCode == userCode);
        if (controllerId is not null) query = query.Where(l => l.ControllerId == controllerId);
        if (method is not null) query = query.Where(l => l.Method == method);
        return query;
    }

    private static string CsvEscape(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Contains(',') || value.Contains('"') || value.Contains('\n')
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;
    }
}
