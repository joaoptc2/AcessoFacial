using HospitalAccess.Api.Services;
using HospitalAccess.Domain.Entities;
using HospitalAccess.Gateway;
using HospitalAccess.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HospitalAccess.Api.Controllers;

/// <summary>
/// Fotos capturadas pelo controlador nos eventos de acesso (Classe XI). A correlação
/// foto↔registro é por ordem cronológica — o SDK não expõe identificador explícito.
/// </summary>
public sealed class ControllerEventPhotosController : ControllerEndpointBase
{
    public ControllerEventPhotosController(AccessDbContext db, IDeviceGateway gateway)
        : base(db, gateway)
    {
    }


    [HttpPost("{id:guid}/event-photos/download")]
    [Authorize(Roles = "Admin,Operator")]
    public async Task<IActionResult> DownloadEventPhotos(Guid id, [FromQuery] int quantity, CancellationToken ct)
    {
        if (quantity is < 1 or > 500) quantity = 20;

        var controller = await Db.Controllers.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (controller is null) return NotFound();

        try
        {
            var photos = await Gateway.ReadRecentEventPhotosAsync(controller, quantity, ct);
            foreach (var p in photos)
            {
                Db.EventPhotos.Add(new EventPhoto
                {
                    ControllerId = controller.Id,
                    ControllerName = controller.Name,
                    UserCode = p.UserCode,
                    CapturedAtUtc = p.CapturedAtUtc,
                    RawEventCode = p.RawEventCode,
                    ImageJpg = p.ImageJpg,
                });
            }
            await Db.SaveChangesAsync(ct);
            return Ok(new { downloaded = photos.Count });
        }
        catch (Exception ex)
        {
            return StatusCode(502, new { error = ex.Message });
        }
    }

    [HttpGet("{id:guid}/event-photos")]
    [Authorize(Roles = "Admin,Operator")]
    public async Task<IActionResult> ListEventPhotos(Guid id, CancellationToken ct)
    {
        var photos = await Db.EventPhotos
            .Where(p => p.ControllerId == id)
            .OrderByDescending(p => p.CapturedAtUtc)
            .Select(p => new { p.Id, p.UserCode, p.CapturedAtUtc, p.RawEventCode, p.DownloadedAtUtc })
            .ToListAsync(ct);
        return Ok(photos);
    }

    [HttpGet("event-photos/{photoId:guid}/image")]
    [Authorize(Roles = "Admin,Operator")]
    public async Task<IActionResult> GetEventPhotoImage(Guid photoId, CancellationToken ct)
    {
        var photo = await Db.EventPhotos.FirstOrDefaultAsync(p => p.Id == photoId, ct);
        if (photo is null) return NotFound();
        return File(photo.ImageJpg, "image/jpeg");
    }
}
