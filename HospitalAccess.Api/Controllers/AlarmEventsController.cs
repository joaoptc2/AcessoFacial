using System.Globalization;
using System.Text;
using HospitalAccess.Domain.Enums;
using HospitalAccess.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HospitalAccess.Api.Controllers;

/// <summary>Consulta do log de alarmes de hardware (incêndio, coação, sabotagem, arrombamento etc.). Append-only, só leitura.</summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = "Admin,Operator")]
public class AlarmEventsController : ControllerBase
{
    private const int MaxPageSize = 500;

    private readonly AccessDbContext _db;

    public AlarmEventsController(AccessDbContext db)
    {
        _db = db;
    }

    [HttpGet]
    public async Task<IActionResult> Query(
        [FromQuery] DateTime? from, [FromQuery] DateTime? to,
        [FromQuery] Guid? controllerId, [FromQuery] AlarmKind? kind,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default)
    {
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);
        page = Math.Max(page, 1);

        var query = BuildFilteredQuery(from, to, controllerId, kind);

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(a => a.TimestampUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return Ok(new { total, page, pageSize, items });
    }

    [HttpGet("export")]
    public async Task<IActionResult> Export(
        [FromQuery] DateTime? from, [FromQuery] DateTime? to,
        [FromQuery] Guid? controllerId, [FromQuery] AlarmKind? kind, CancellationToken ct = default)
    {
        var items = await BuildFilteredQuery(from, to, controllerId, kind)
            .OrderByDescending(a => a.TimestampUtc)
            .ToListAsync(ct);

        var csv = new StringBuilder();
        csv.AppendLine("TimestampUtc,ControllerName,Kind,RawEventCode,Cleared");
        foreach (var a in items)
        {
            csv.AppendLine(string.Join(',',
                a.TimestampUtc.ToString("O", CultureInfo.InvariantCulture),
                CsvEscape(a.ControllerName),
                a.Kind.ToString(),
                a.RawEventCode.ToString(CultureInfo.InvariantCulture),
                a.Cleared.ToString()));
        }

        var bytes = Encoding.UTF8.GetBytes(csv.ToString());
        return File(bytes, "text/csv", $"alarm-events-{DateTime.UtcNow:yyyyMMddHHmmss}.csv");
    }

    private IQueryable<Domain.Entities.AlarmEvent> BuildFilteredQuery(DateTime? from, DateTime? to, Guid? controllerId, AlarmKind? kind)
    {
        from = ToUtc(from);
        to = ToUtc(to);

        var query = _db.AlarmEvents.AsNoTracking().AsQueryable();
        if (from is not null) query = query.Where(a => a.TimestampUtc >= from);
        if (to is not null) query = query.Where(a => a.TimestampUtc <= to);
        if (controllerId is not null) query = query.Where(a => a.ControllerId == controllerId);
        if (kind is not null) query = query.Where(a => a.Kind == kind);
        return query;
    }

    private static DateTime? ToUtc(DateTime? value) => value?.Kind switch
    {
        null => null,
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.Value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value!.Value, DateTimeKind.Utc),
    };

    private static string CsvEscape(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        // Neutraliza CSV/formula injection (célula iniciando com = + - @ / tab / CR).
        var sanitized = "=+-@\t\r".Contains(value[0]) ? "'" + value : value;
        return sanitized.Contains(',') || sanitized.Contains('"') || sanitized.Contains('\n')
            ? $"\"{sanitized.Replace("\"", "\"\"")}\""
            : sanitized;
    }
}
