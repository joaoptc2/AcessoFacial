using HospitalAccess.Domain.Entities;
using HospitalAccess.Gateway;
using HospitalAccess.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HospitalAccess.Api.Controllers;

public record TimeGroupSegmentRequest(byte Weekday, byte SegmentIndex, TimeOnly BeginTime, TimeOnly EndTime);
public record TimeGroupScheduleRequest(int GroupNumber, string Name, List<TimeGroupSegmentRequest> Segments);

/// <summary>
/// Grade horária (Classe VI do protocolo) — define o que cada TimeGroup (1-64, referenciado
/// por User.TimeGroup) significa em termos de horário. Definição global mantida no banco e
/// empurrada para os controladores sob demanda (não há leitura de volta do dispositivo).
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = "Admin,Operator")]
public class TimeGroupsController : ControllerBase
{
    private readonly AccessDbContext _db;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TimeGroupsController> _logger;

    public TimeGroupsController(AccessDbContext db, IServiceScopeFactory scopeFactory, ILogger<TimeGroupsController> logger)
    {
        _db = db;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var schedules = await _db.TimeGroupSchedules
            .Include(t => t.Segments)
            .OrderBy(t => t.GroupNumber)
            .Select(t => new
            {
                t.Id,
                t.GroupNumber,
                t.Name,
                Segments = t.Segments.Select(s => new { s.Weekday, s.SegmentIndex, s.BeginTime, s.EndTime }),
            })
            .ToListAsync(ct);
        return Ok(schedules);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] TimeGroupScheduleRequest request, CancellationToken ct)
    {
        if (request.GroupNumber is < 1 or > 64) return BadRequest("GroupNumber deve estar entre 1 e 64 (capacidade do dispositivo).");
        if (await _db.TimeGroupSchedules.AnyAsync(t => t.GroupNumber == request.GroupNumber, ct))
            return Conflict($"Já existe uma grade para o grupo {request.GroupNumber}.");
        if (!ValidateSegments(request.Segments, out var error)) return BadRequest(error);

        var schedule = new TimeGroupSchedule { GroupNumber = request.GroupNumber, Name = request.Name };
        foreach (var seg in request.Segments)
        {
            schedule.Segments.Add(new TimeGroupSegment
            {
                Weekday = seg.Weekday,
                SegmentIndex = seg.SegmentIndex,
                BeginTime = seg.BeginTime,
                EndTime = seg.EndTime,
            });
        }

        _db.TimeGroupSchedules.Add(schedule);
        await _db.SaveChangesAsync(ct);

        return CreatedAtAction(nameof(List), new { id = schedule.Id }, new { schedule.Id });
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] TimeGroupScheduleRequest request, CancellationToken ct)
    {
        var schedule = await _db.TimeGroupSchedules.Include(t => t.Segments).FirstOrDefaultAsync(t => t.Id == id, ct);
        if (schedule is null) return NotFound();
        if (request.GroupNumber is < 1 or > 64) return BadRequest("GroupNumber deve estar entre 1 e 64 (capacidade do dispositivo).");
        if (await _db.TimeGroupSchedules.AnyAsync(t => t.GroupNumber == request.GroupNumber && t.Id != id, ct))
            return Conflict($"Já existe uma grade para o grupo {request.GroupNumber}.");
        if (!ValidateSegments(request.Segments, out var error)) return BadRequest(error);

        schedule.GroupNumber = request.GroupNumber;
        schedule.Name = request.Name;

        _db.TimeGroupSegments.RemoveRange(schedule.Segments);
        schedule.Segments.Clear();
        foreach (var seg in request.Segments)
        {
            // _db.Add (não schedule.Segments.Add): como TimeGroupSegment.Id já vem preenchido
            // (Guid.NewGuid() no inicializador), o EF Core marca entidades adicionadas via fixup
            // de uma coleção navigation já rastreada como Modified em vez de Added — gera UPDATE
            // em vez de INSERT e lança DbUpdateConcurrencyException (0 linhas afetadas). DbSet.Add
            // força o estado Added independente do valor da chave.
            _db.TimeGroupSegments.Add(new TimeGroupSegment
            {
                TimeGroupScheduleId = schedule.Id,
                Weekday = seg.Weekday,
                SegmentIndex = seg.SegmentIndex,
                BeginTime = seg.BeginTime,
                EndTime = seg.EndTime,
            });
        }

        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var schedule = await _db.TimeGroupSchedules.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (schedule is null) return NotFound();

        _db.TimeGroupSchedules.Remove(schedule);
        await _db.SaveChangesAsync(ct);

        return NoContent();
    }

    /// <summary>Substitui os 64 grupos de horário em todos os controladores. Roda em segundo plano (30 dispositivos).</summary>
    [HttpPost("sync-all")]
    public async Task<IActionResult> SyncAll(CancellationToken ct)
    {
        var controllerIds = await _db.Controllers.Select(c => c.Id).ToListAsync(ct);
        SyncInBackground(controllerIds);
        return Accepted(new { controllerCount = controllerIds.Count });
    }

    private static bool ValidateSegments(List<TimeGroupSegmentRequest> segments, out string? error)
    {
        foreach (var seg in segments)
        {
            if (seg.Weekday > 6) { error = "Weekday deve estar entre 0 (segunda) e 6 (domingo)."; return false; }
            if (seg.SegmentIndex > 7) { error = "SegmentIndex deve estar entre 0 e 7 (até 8 janelas por dia)."; return false; }
            if (seg.EndTime <= seg.BeginTime) { error = "EndTime deve ser depois de BeginTime."; return false; }
        }
        error = null;
        return true;
    }

    private void SyncInBackground(List<Guid> controllerIds)
    {
        _ = Task.Run(async () =>
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AccessDbContext>();
            var gateway = scope.ServiceProvider.GetRequiredService<IDeviceGateway>();

            var schedules = await db.TimeGroupSchedules.Include(t => t.Segments).ToListAsync();
            var entries = schedules.Select(t => new TimeGroupEntry(
                t.GroupNumber,
                t.Segments.Select(s => new TimeGroupSegmentEntry(s.Weekday, s.SegmentIndex, s.BeginTime, s.EndTime)).ToList())).ToList();

            foreach (var controllerId in controllerIds)
            {
                try
                {
                    var controller = await db.Controllers.FirstOrDefaultAsync(c => c.Id == controllerId);
                    if (controller is null) continue;
                    await gateway.SyncTimeGroupsAsync(controller, entries);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Falha ao sincronizar grade horária no controlador {ControllerId}.", controllerId);
                }
            }
        });
    }
}
