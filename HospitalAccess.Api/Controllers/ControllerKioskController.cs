using HospitalAccess.Api.Services;
using HospitalAccess.Domain.Entities;
using HospitalAccess.Gateway;
using HospitalAccess.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HospitalAccess.Api.Controllers;

/// <summary>Ajustes locais do quiosque: idioma, volume, luz, máscara, temperatura, vivacidade.</summary>
public sealed class ControllerKioskController : ControllerEndpointBase
{
    public ControllerKioskController(AccessDbContext db, IDeviceGateway gateway)
        : base(db, gateway)
    {
    }

    [HttpGet("{id:guid}/kiosk-settings")]
    [Authorize(Roles = "Admin,Operator")]
    public Task<IActionResult> GetKioskSettings(Guid id, CancellationToken ct) => RunReadAsync(id, Gateway.ReadKioskSettingsAsync, ct);

    [HttpPut("{id:guid}/kiosk-settings")]
    [Authorize(Roles = "Admin,Operator")]
    public Task<IActionResult> PutKioskSettings(Guid id, [FromBody] KioskSettingsSnapshot settings, CancellationToken ct) =>
        RunWriteAsync(id, (c, ct2) => Gateway.WriteKioskSettingsAsync(c, settings, ct2), ct);
}
