using HospitalAccess.Api.Services;
using HospitalAccess.Domain.Entities;
using HospitalAccess.Gateway;
using HospitalAccess.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HospitalAccess.Api.Controllers;

/// <summary>
/// Relógio do controlador. A validade de acesso (Person.Expiry) é gravada em horário LOCAL do
/// aparelho, então um relógio fora do lugar faz credencial válida ser recusada — e vice-versa.
/// </summary>
public sealed class ControllerClockController : ControllerEndpointBase
{
    public ControllerClockController(AccessDbContext db, IDeviceGateway gateway)
        : base(db, gateway)
    {
    }

    [HttpGet("{id:guid}/clock")]
    [Authorize(Roles = "Admin,Operator")]
    public Task<IActionResult> GetClock(Guid id, CancellationToken ct) => RunReadAsync(id, Gateway.ReadControllerTimeAsync, ct);

    [HttpPost("{id:guid}/clock/sync")]
    [Authorize(Roles = "Admin,Operator")]
    public async Task<IActionResult> SyncClock(Guid id, CancellationToken ct)
    {
        var controller = await Db.Controllers.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (controller is null) return NotFound();

        try
        {
            await Gateway.SyncControllerTimeAsync(controller, ct);
            controller.LastClockSyncAtUtc = DateTime.UtcNow;
            await Db.SaveChangesAsync(ct);
            return NoContent();
        }
        catch (Exception ex)
        {
            return StatusCode(502, new { error = ex.Message });
        }
    }
}
