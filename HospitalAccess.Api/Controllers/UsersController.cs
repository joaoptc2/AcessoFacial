using HospitalAccess.Application.Sync;
using HospitalAccess.Domain.Entities;
using HospitalAccess.Domain.Enums;
using HospitalAccess.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HospitalAccess.Api.Controllers;

public record CreateUserRequest(string Name, int TimeGroup, Guid[]? DoorIds = null);

/// <summary>Cadastro de usuários permanentes (face) e disparo de sincronização.</summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = "Admin,Operator")]
public class UsersController : ControllerBase
{
    private readonly AccessDbContext _db;
    private readonly IUserSyncService _sync;

    public UsersController(AccessDbContext db, IUserSyncService sync)
    {
        _db = db;
        _sync = sync;
    }

    /// <summary>Cria usuário com foto (JPG). A foto é convertida para 480x640/<=120KB no gateway.</summary>
    [HttpPost]
    [RequestSizeLimit(10 * 1024 * 1024)]
    public async Task<IActionResult> Create(
        [FromForm] CreateUserRequest request, [FromForm] IFormFile facePhoto, CancellationToken ct)
    {
        if (facePhoto.Length == 0)
            return BadRequest("facePhoto é obrigatório.");
        if (request.TimeGroup is < 1 or > 64)
            return BadRequest("TimeGroup deve estar entre 1 e 64.");

        await using var ms = new MemoryStream();
        await facePhoto.CopyToAsync(ms, ct);

        var nextCode = (await _db.Users.MaxAsync(u => (uint?)u.UserCode, ct) ?? 0) + 1;

        var user = new User
        {
            UserCode = nextCode,
            Name = request.Name,
            Type = UserType.Permanent,
            TimeGroup = request.TimeGroup,
            FacePhoto = ms.ToArray(),
            CreatedByUsername = CurrentUsername(),
        };

        foreach (var doorId in (request.DoorIds ?? []).Distinct())
        {
            var doorExists = await _db.Doors.AnyAsync(d => d.Id == doorId, ct);
            if (!doorExists) return BadRequest($"Porta {doorId} não existe.");
            user.Permissions.Add(new AccessPermission { DoorId = doorId, TimeGroup = request.TimeGroup });
        }

        _db.Users.Add(user);
        await _db.SaveChangesAsync(ct);

        await _sync.SyncUserAsync(user.Id, ct);

        return CreatedAtAction(nameof(Create), new { id = user.Id }, new { user.Id, user.UserCode });
    }

    /// <summary>Revoga um usuário permanente (remove dos controladores).</summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Revoke(Guid id, CancellationToken ct)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null) return NotFound();

        await _sync.RevokeUserAsync(id, ct);

        user.RevokedByUsername = CurrentUsername();
        user.RevokedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        return NoContent();
    }

    private string? CurrentUsername() => User.Identity?.Name;
}
