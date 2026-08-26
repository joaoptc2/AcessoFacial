using HospitalAccess.Api.Services;
using HospitalAccess.Domain.Entities;
using HospitalAccess.Gateway;
using HospitalAccess.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HospitalAccess.Api.Controllers;

/// <summary>Alarmes de hardware do controlador: incêndio, coação, sabotagem, porta forçada.</summary>
public sealed class ControllerAlarmsController : ControllerEndpointBase
{
    public ControllerAlarmsController(AccessDbContext db, IDeviceGateway gateway)
        : base(db, gateway)
    {
    }

    [HttpGet("{id:guid}/alarm-settings")]
    [Authorize(Roles = "Admin,Operator")]
    public Task<IActionResult> GetAlarmSettings(Guid id, CancellationToken ct) => RunReadAsync(id, Gateway.ReadAlarmSettingsAsync, ct);

    [HttpPut("{id:guid}/alarm-settings")]
    [Authorize(Roles = "Admin,Operator")]
    public Task<IActionResult> PutAlarmSettings(Guid id, [FromBody] AlarmSettingsSnapshot settings, CancellationToken ct) =>
        RunWriteAsync(id, (c, ct2) => Gateway.WriteAlarmSettingsAsync(c, settings, ct2), ct);

    [HttpPost("{id:guid}/alarm-clear")]
    [Authorize(Roles = "Admin,Operator")]
    public Task<IActionResult> ClearAlarm(Guid id, CancellationToken ct) => RunWriteAsync(id, Gateway.ClearAlarmAsync, ct);

    /// <summary>Dispara o alarme de incêndio NESTE controlador (auditado).</summary>
    [HttpPost("{id:guid}/alarm-fire")]
    [Authorize(Roles = "Admin,Operator")]
    public async Task<IActionResult> TriggerFireAlarm(Guid id, CancellationToken ct)
    {
        var controller = await Db.Controllers.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (controller is null) return NotFound();
        try
        {
            await Gateway.TriggerFireAlarmAsync(controller, ct);
            await AuditAsync(controller, "DispararIncêndio", success: true, error: null, ct);
            return NoContent();
        }
        catch (Exception ex)
        {
            await AuditAsync(controller, "DispararIncêndio", success: false, error: ex.Message, ct);
            return StatusCode(502, new { error = ex.Message });
        }
    }
}
