using HospitalAccess.Api.Services;
using HospitalAccess.Domain.Entities;
using HospitalAccess.Domain.Enums;
using HospitalAccess.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HospitalAccess.Api.Controllers;

public record CreateStaffUserRequest(string Username, string Password, StaffRole Role);
public record UpdateStaffUserRequest(StaffRole Role, bool Active);
public record ResetStaffPasswordRequest(string NewPassword);

/// <summary>
/// Gestão dos USUÁRIOS DO SISTEMA (logins da aplicação) e seus cargos — Admin (tudo),
/// Operator (opera dispositivos e cadastros) e Reception (consulta + visitantes). Sem DELETE
/// físico: desativar preserva a autoria histórica nos logs de auditoria. Mudança de cargo passa
/// a valer no PRÓXIMO login (o cargo viaja no token JWT). Só Admin.
/// </summary>
[ApiController]
[Route("api/staffusers")]
[Authorize(Roles = "Admin")]
public class StaffUsersController : ControllerBase
{
    private const int MinPasswordLength = 8;

    private readonly AccessDbContext _db;
    private readonly PasswordHasher<StaffUser> _hasher = new();

    public StaffUsersController(AccessDbContext db)
    {
        _db = db;
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var users = await _db.StaffUsers.AsNoTracking()
            .OrderBy(s => s.Username)
            .Select(s => new { s.Id, s.Username, Role = s.Role.ToString(), s.Active })
            .ToListAsync(ct);
        return Ok(users);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateStaffUserRequest request, CancellationToken ct)
    {
        var username = (request.Username ?? string.Empty).Trim();
        if (username.Length is < 3 or > 64)
            return BadRequest("O nome de usuário deve ter entre 3 e 64 caracteres.");
        if ((request.Password ?? string.Empty).Length < MinPasswordLength)
            return BadRequest($"A senha deve ter pelo menos {MinPasswordLength} caracteres.");
        if (await _db.StaffUsers.AnyAsync(s => s.Username == username, ct))
            return Conflict("Já existe um usuário com este nome.");

        var user = new StaffUser { Username = username, Role = request.Role, Active = true };
        user.PasswordHash = _hasher.HashPassword(user, request.Password!);
        _db.StaffUsers.Add(user);
        await _db.SaveChangesAsync(ct);

        return CreatedAtAction(nameof(List), null, new { user.Id, user.Username, Role = user.Role.ToString(), user.Active });
    }

    /// <summary>Muda cargo e/ou ativo. Guardas: nunca remover o último Admin ativo; nunca se trancar para fora.</summary>
    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateStaffUserRequest request, CancellationToken ct)
    {
        var user = await _db.StaffUsers.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (user is null) return NotFound();

        var isSelf = string.Equals(user.Username, User.Identity?.Name, StringComparison.Ordinal);
        if (StaffUserRules.IsSelfLockout(isSelf, user.Role, request.Role, request.Active))
            return BadRequest("Você não pode rebaixar nem desativar o próprio usuário — peça a outro Admin.");

        var isLastActiveAdmin = user.Role == StaffRole.Admin && user.Active &&
            !await _db.StaffUsers.AnyAsync(s => s.Id != id && s.Role == StaffRole.Admin && s.Active, ct);
        if (!StaffUserRules.CanChange(user.Role, user.Active, request.Role, request.Active, isLastActiveAdmin, out var reason))
            return BadRequest(reason);

        user.Role = request.Role;
        user.Active = request.Active;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/reset-password")]
    public async Task<IActionResult> ResetPassword(Guid id, [FromBody] ResetStaffPasswordRequest request, CancellationToken ct)
    {
        if ((request.NewPassword ?? string.Empty).Length < MinPasswordLength)
            return BadRequest($"A senha deve ter pelo menos {MinPasswordLength} caracteres.");

        var user = await _db.StaffUsers.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (user is null) return NotFound();

        user.PasswordHash = _hasher.HashPassword(user, request.NewPassword!);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }
}
