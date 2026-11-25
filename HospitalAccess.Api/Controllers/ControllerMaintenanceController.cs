using HospitalAccess.Gateway;
using HospitalAccess.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HospitalAccess.Api.Controllers;

/// <summary>Ações de manutenção do controlador que não se encaixam em alarmes, rede ou auditoria de pessoal.</summary>
public sealed class ControllerMaintenanceController : ControllerEndpointBase
{
    public ControllerMaintenanceController(AccessDbContext db, IDeviceGateway gateway)
        : base(db, gateway)
    {
    }

    /// <summary>Reinicia o controlador agora (auditado). O aparelho fica fora do ar durante o boot físico.</summary>
    [HttpPost("{id:guid}/reboot")]
    [Authorize(Roles = "Admin,Operator")]
    public async Task<IActionResult> Reboot(Guid id, CancellationToken ct)
    {
        var controller = await Db.Controllers.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (controller is null) return NotFound();
        try
        {
            await Gateway.RebootAsync(controller, ct);
            await AuditAsync(controller, "Reiniciar", success: true, error: null, ct);
            return NoContent();
        }
        catch (Exception ex)
        {
            await AuditAsync(controller, "Reiniciar", success: false, error: ex.Message, ct);
            return StatusCode(502, new { error = ex.Message });
        }
    }
}
