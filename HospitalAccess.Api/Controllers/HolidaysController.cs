using HospitalAccess.Api.Services;
using HospitalAccess.Domain.Entities;
using HospitalAccess.Gateway;
using HospitalAccess.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HospitalAccess.Api.Controllers;

public record HolidayRequest(byte Index, string Name, DateTime Date, bool RepeatsYearly, byte HolidayType);

/// <summary>
/// Calendário de feriados (Classe V do protocolo), definição global mantida no banco e
/// empurrada para os controladores sob demanda (não há leitura de volta do dispositivo).
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = "Admin,Operator")]
public class HolidaysController : ControllerBase
{
    private readonly AccessDbContext _db;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SingleFlight _singleFlight;
    private readonly ILogger<HolidaysController> _logger;

    public HolidaysController(AccessDbContext db, IServiceScopeFactory scopeFactory,
        SingleFlight singleFlight, ILogger<HolidaysController> logger)
    {
        _db = db;
        _scopeFactory = scopeFactory;
        _singleFlight = singleFlight;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var holidays = await _db.Holidays.OrderBy(h => h.Index).ToListAsync(ct);
        return Ok(holidays);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] HolidayRequest request, CancellationToken ct)
    {
        if (request.Index is < 1 or > 30) return BadRequest("Index deve estar entre 1 e 30 (capacidade do dispositivo).");
        if (await _db.Holidays.AnyAsync(h => h.Index == request.Index, ct))
            return Conflict($"Já existe um feriado no slot {request.Index}.");

        var holiday = new Holiday
        {
            Index = request.Index,
            Name = request.Name,
            Date = request.Date,
            RepeatsYearly = request.RepeatsYearly,
            HolidayType = request.HolidayType,
        };
        _db.Holidays.Add(holiday);
        await _db.SaveChangesAsync(ct);

        return CreatedAtAction(nameof(List), new { id = holiday.Id }, new { holiday.Id });
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] HolidayRequest request, CancellationToken ct)
    {
        var holiday = await _db.Holidays.FirstOrDefaultAsync(h => h.Id == id, ct);
        if (holiday is null) return NotFound();
        if (request.Index is < 1 or > 30) return BadRequest("Index deve estar entre 1 e 30 (capacidade do dispositivo).");
        if (await _db.Holidays.AnyAsync(h => h.Index == request.Index && h.Id != id, ct))
            return Conflict($"Já existe um feriado no slot {request.Index}.");

        holiday.Index = request.Index;
        holiday.Name = request.Name;
        holiday.Date = request.Date;
        holiday.RepeatsYearly = request.RepeatsYearly;
        holiday.HolidayType = request.HolidayType;
        await _db.SaveChangesAsync(ct);

        return NoContent();
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var holiday = await _db.Holidays.FirstOrDefaultAsync(h => h.Id == id, ct);
        if (holiday is null) return NotFound();

        _db.Holidays.Remove(holiday);
        await _db.SaveChangesAsync(ct);

        return NoContent();
    }

    /// <summary>Substitui os feriados em todos os controladores pela lista atual do banco. Roda em segundo plano (30 dispositivos).</summary>
    [HttpPost("sync-all")]
    public async Task<IActionResult> SyncAll(CancellationToken ct)
    {
        // Single-flight global: cliques repetidos não empilham varreduras concorrentes dos 30
        // aparelhos (dentro de cada varredura o loop já é sequencial).
        if (!_singleFlight.TryBegin(FlightKey))
            return Conflict(new { error = "Já existe uma sincronização de feriados em andamento." });

        List<Guid> controllerIds;
        try
        {
            controllerIds = await _db.Controllers.Select(c => c.Id).ToListAsync(ct);
        }
        catch
        {
            // Falha ANTES de agendar o job: liberar a chave, senão o endpoint fica preso em 409.
            _singleFlight.End(FlightKey);
            throw;
        }

        SyncInBackground(controllerIds);
        return Accepted(new { controllerCount = controllerIds.Count });
    }

    private const string FlightKey = "holidays:sync-all";

    private void SyncInBackground(List<Guid> controllerIds)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AccessDbContext>();
                var gateway = scope.ServiceProvider.GetRequiredService<IDeviceGateway>();

                var holidays = await db.Holidays.OrderBy(h => h.Index).ToListAsync();
                var entries = holidays
                    .Select(h => new HolidayEntry(h.Index, h.Date, h.RepeatsYearly, h.HolidayType))
                    .ToList();

                foreach (var controllerId in controllerIds)
                {
                    try
                    {
                        var controller = await db.Controllers.FirstOrDefaultAsync(c => c.Id == controllerId);
                        if (controller is null) continue;
                        await gateway.SyncHolidaysAsync(controller, entries);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Falha ao sincronizar feriados no controlador {ControllerId}.", controllerId);
                    }
                }
            }
            finally
            {
                _singleFlight.End(FlightKey);
            }
        });
    }
}
