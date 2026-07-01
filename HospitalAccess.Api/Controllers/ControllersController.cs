using HospitalAccess.Domain.Entities;
using HospitalAccess.Gateway;
using HospitalAccess.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HospitalAccess.Api.Controllers;

public record CreateControllerRequest(
    string Name, string IpAddress, int Port, string SerialNumber,
    string CommunicationPassword, bool SupportsWaitRepeatMessage);

/// <summary>Cadastro dos 30 controladores 8190H e status de sincronização por dispositivo.</summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = "Admin,Operator")]
public class ControllersController : ControllerBase
{
    private readonly AccessDbContext _db;
    private readonly IDeviceGateway _gateway;

    public ControllersController(AccessDbContext db, IDeviceGateway gateway)
    {
        _db = db;
        _gateway = gateway;
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var controllers = await _db.Controllers
            .Select(c => new
            {
                c.Id, c.Name, c.IpAddress, c.Port, c.SerialNumber, c.SupportsWaitRepeatMessage,
                Doors = c.Doors.Select(d => new { d.Id, d.Name, d.RelayIndex }),
            })
            .ToListAsync(ct);
        return Ok(controllers);
    }

    /// <summary>Status de sincronização (DeviceSyncStatus) de todos os usuários neste controlador.</summary>
    [HttpGet("{id:guid}/sync-status")]
    public async Task<IActionResult> SyncStatus(Guid id, CancellationToken ct)
    {
        var statuses = await _db.SyncStatuses
            .Where(s => s.ControllerId == id)
            .Include(s => s.User)
            .Select(s => new
            {
                s.UserId, UserName = s.User!.Name, s.State, s.RetryCount, s.LastError, s.UpdatedAt,
            })
            .ToListAsync(ct);
        return Ok(statuses);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateControllerRequest request, CancellationToken ct)
    {
        if (request.SerialNumber.Length != 16)
            return BadRequest("SerialNumber deve ter exatamente 16 dígitos.");

        var controller = new HospitalAccess.Domain.Entities.Controller
        {
            Name = request.Name,
            IpAddress = request.IpAddress,
            Port = request.Port,
            SerialNumber = request.SerialNumber,
            CommunicationPassword = request.CommunicationPassword,
            SupportsWaitRepeatMessage = request.SupportsWaitRepeatMessage,
        };

        _db.Controllers.Add(controller);
        await _db.SaveChangesAsync(ct);

        return CreatedAtAction(nameof(List), new { id = controller.Id }, new { controller.Id });
    }

    /// <summary>Prova de conceito de conectividade: lê o SN reportado pelo controlador (ReadSN) e confere com o cadastrado.</summary>
    [HttpPost("{id:guid}/test-connection")]
    public async Task<IActionResult> TestConnection(Guid id, CancellationToken ct)
    {
        var controller = await _db.Controllers.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (controller is null) return NotFound();

        try
        {
            var sn = await _gateway.ReadSerialNumberAsync(controller, ct);
            return Ok(new { reportedSerialNumber = sn, matchesRegistered = sn == controller.SerialNumber });
        }
        catch (Exception ex)
        {
            return StatusCode(502, new { error = ex.Message });
        }
    }
}
