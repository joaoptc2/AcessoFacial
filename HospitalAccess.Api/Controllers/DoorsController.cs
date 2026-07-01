using HospitalAccess.Domain.Entities;
using HospitalAccess.Gateway;
using HospitalAccess.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HospitalAccess.Api.Controllers;

public record CreateDoorRequest(string Name, Guid ControllerId, int RelayIndex);

/// <summary>Comando remoto de portas e consulta de estado.</summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = "Admin,Operator,Reception")]
public class DoorsController : ControllerBase
{
    private readonly AccessDbContext _db;
    private readonly IDeviceGateway _gateway;

    public DoorsController(AccessDbContext db, IDeviceGateway gateway)
    {
        _db = db;
        _gateway = gateway;
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var doors = await _db.Doors
            .Include(d => d.Controller)
            .Select(d => new { d.Id, d.Name, d.RelayIndex, ControllerName = d.Controller!.Name })
            .ToListAsync(ct);
        return Ok(doors);
    }

    [HttpPost]
    [Authorize(Roles = "Admin,Operator")]
    public async Task<IActionResult> Create([FromBody] CreateDoorRequest request, CancellationToken ct)
    {
        var controllerExists = await _db.Controllers.AnyAsync(c => c.Id == request.ControllerId, ct);
        if (!controllerExists) return BadRequest("Controlador não existe.");

        var door = new Door { Name = request.Name, ControllerId = request.ControllerId, RelayIndex = request.RelayIndex };
        _db.Doors.Add(door);
        await _db.SaveChangesAsync(ct);

        return CreatedAtAction(nameof(List), new { id = door.Id }, new { door.Id });
    }

    [HttpPost("{doorId:guid}/open")]
    public async Task<IActionResult> Open(Guid doorId, CancellationToken ct)
    {
        var door = await _db.Doors.Include(d => d.Controller).FirstOrDefaultAsync(d => d.Id == doorId, ct);
        if (door?.Controller is null) return NotFound();

        await _gateway.OpenDoorAsync(door.Controller, door.RelayIndex, ct);
        return NoContent();
    }
}
