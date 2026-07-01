using HospitalAccess.Application.Sync;
using HospitalAccess.Domain.Entities;
using HospitalAccess.Domain.Enums;
using HospitalAccess.Gateway;
using HospitalAccess.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HospitalAccess.Api.Controllers;

public record CreateUserRequest(string Name, int TimeGroup, Guid? GroupId, Guid[]? ControllerIds = null);
public record UpdateUserRequest(string Name, int TimeGroup, Guid? GroupId, Guid[]? ControllerIds = null);

/// <summary>Cadastro/gestão de usuários permanentes (acesso por face) e disparo de sincronização.</summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = "Admin,Operator")]
public class UsersController : ControllerBase
{
    private readonly AccessDbContext _db;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<UsersController> _logger;

    public UsersController(AccessDbContext db, IServiceScopeFactory scopeFactory, ILogger<UsersController> logger)
    {
        _db = db;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>Lista os usuários permanentes (visitantes ficam em /api/visitors).</summary>
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var users = await _db.Users
            .Where(u => u.Type == UserType.Permanent)
            .Include(u => u.Group)
            .Include(u => u.Permissions).ThenInclude(p => p.Controller)
            .OrderBy(u => u.Name)
            .Select(u => new
            {
                u.Id,
                u.UserCode,
                u.Name,
                u.TimeGroup,
                u.GroupId,
                GroupName = u.Group != null ? u.Group.Name : null,
                HasFacePhoto = u.FacePhoto != null,
                u.CreatedByUsername,
                u.CreatedAtUtc,
                u.RevokedAtUtc,
                Controllers = u.Permissions.Select(p => new { p.ControllerId, ControllerName = p.Controller!.Name }),
            })
            .ToListAsync(ct);

        return Ok(users);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var user = await _db.Users
            .Include(u => u.Permissions)
            .FirstOrDefaultAsync(u => u.Id == id && u.Type == UserType.Permanent, ct);
        if (user is null) return NotFound();

        return Ok(new
        {
            user.Id,
            user.UserCode,
            user.Name,
            user.TimeGroup,
            user.GroupId,
            HasFacePhoto = user.FacePhoto != null,
            ControllerIds = user.Permissions.Select(p => p.ControllerId),
        });
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
        if (request.GroupId is { } groupId && !await _db.UserGroups.AnyAsync(g => g.Id == groupId, ct))
            return BadRequest("Grupo não existe.");

        await using var ms = new MemoryStream();
        await facePhoto.CopyToAsync(ms, ct);

        var nextCode = (await _db.Users.MaxAsync(u => (uint?)u.UserCode, ct) ?? 0) + 1;

        var user = new User
        {
            UserCode = nextCode,
            Name = request.Name,
            Type = UserType.Permanent,
            TimeGroup = request.TimeGroup,
            GroupId = request.GroupId,
            FacePhoto = ms.ToArray(),
            CreatedByUsername = CurrentUsername(),
        };

        foreach (var controllerId in (request.ControllerIds ?? []).Distinct())
        {
            if (!await _db.Controllers.AnyAsync(c => c.Id == controllerId, ct))
                return BadRequest($"Controlador {controllerId} não existe.");
            user.Permissions.Add(new AccessPermission { ControllerId = controllerId, TimeGroup = request.TimeGroup });
        }

        _db.Users.Add(user);
        await _db.SaveChangesAsync(ct);

        SyncInBackground(user.Id);

        return CreatedAtAction(nameof(Get), new { id = user.Id }, new { user.Id, user.UserCode });
    }

    /// <summary>Atualiza nome, grupo de horário, grupo organizacional, portas e (opcionalmente) a foto.</summary>
    [HttpPut("{id:guid}")]
    [RequestSizeLimit(10 * 1024 * 1024)]
    public async Task<IActionResult> Update(
        Guid id, [FromForm] UpdateUserRequest request, IFormFile? facePhoto, CancellationToken ct)
    {
        var user = await _db.Users
            .Include(u => u.Permissions)
            .FirstOrDefaultAsync(u => u.Id == id && u.Type == UserType.Permanent, ct);
        if (user is null) return NotFound();

        if (request.TimeGroup is < 1 or > 64)
            return BadRequest("TimeGroup deve estar entre 1 e 64.");
        if (request.GroupId is { } groupId && !await _db.UserGroups.AnyAsync(g => g.Id == groupId, ct))
            return BadRequest("Grupo não existe.");

        user.Name = request.Name;
        user.TimeGroup = request.TimeGroup;
        user.GroupId = request.GroupId;

        if (facePhoto is { Length: > 0 })
        {
            await using var ms = new MemoryStream();
            await facePhoto.CopyToAsync(ms, ct);
            user.FacePhoto = ms.ToArray();
        }

        var desiredControllerIds = (request.ControllerIds ?? []).Distinct().ToHashSet();
        foreach (var controllerId in desiredControllerIds)
        {
            if (!await _db.Controllers.AnyAsync(c => c.Id == controllerId, ct))
                return BadRequest($"Controlador {controllerId} não existe.");
        }

        // Remove permissões que não estão mais na lista desejada; adiciona as novas.
        var toRemove = user.Permissions.Where(p => !desiredControllerIds.Contains(p.ControllerId)).ToList();
        foreach (var p in toRemove) _db.Permissions.Remove(p);

        var existingControllerIds = user.Permissions.Select(p => p.ControllerId).ToHashSet();
        foreach (var controllerId in desiredControllerIds.Where(cId => !existingControllerIds.Contains(cId)))
        {
            user.Permissions.Add(new AccessPermission { ControllerId = controllerId, TimeGroup = request.TimeGroup });
        }
        foreach (var p in user.Permissions.Where(p => desiredControllerIds.Contains(p.ControllerId)))
        {
            p.TimeGroup = request.TimeGroup;
        }

        await _db.SaveChangesAsync(ct);
        SyncInBackground(user.Id);

        return NoContent();
    }

    /// <summary>Remove o usuário: apaga o cadastro e revoga (em segundo plano) nos controladores. O log de acessos já gravado não é afetado (guarda nome/código como snapshot).</summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == id && u.Type == UserType.Permanent, ct);
        if (user is null) return NotFound();

        // Captura o necessário para revogar no hardware antes de apagar a linha (e o
        // DeviceSyncStatus em cascata), já que o RevokeUserAsync(userId) depende dessas
        // linhas ainda existirem.
        var userCode = user.UserCode;
        var controllerIds = await _db.Permissions
            .Where(p => p.UserId == id)
            .Select(p => p.ControllerId)
            .ToListAsync(ct);

        _db.Users.Remove(user);
        await _db.SaveChangesAsync(ct);

        RevokeDeletedUserInBackground(userCode, controllerIds);

        return NoContent();
    }

    /// <summary>
    /// Roda a sincronização com os controladores fora do ciclo de requisição: os comandos ao
    /// hardware são TCP com retries e podem levar minutos se um controlador estiver inacessível —
    /// não devem bloquear a resposta HTTP. Progresso fica em DeviceSyncStatus (Pending/Synced/Failed),
    /// reprocessado por IUserSyncService.RetryPendingAsync.
    /// </summary>
    private void SyncInBackground(Guid userId)
    {
        _ = Task.Run(async () =>
        {
            using var scope = _scopeFactory.CreateScope();
            var sync = scope.ServiceProvider.GetRequiredService<IUserSyncService>();
            try
            {
                await sync.SyncUserAsync(userId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha ao sincronizar usuário {UserId} em segundo plano.", userId);
            }
        });
    }

    /// <summary>Revoga um usuário já excluído do banco diretamente nos controladores informados (sem depender de linhas de Users/DeviceSyncStatus, que já foram removidas).</summary>
    private void RevokeDeletedUserInBackground(uint userCode, List<Guid> controllerIds)
    {
        if (controllerIds.Count == 0) return;

        _ = Task.Run(async () =>
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AccessDbContext>();
            var gateway = scope.ServiceProvider.GetRequiredService<IDeviceGateway>();

            foreach (var controllerId in controllerIds)
            {
                try
                {
                    var controller = await db.Controllers.FirstOrDefaultAsync(c => c.Id == controllerId);
                    if (controller is null) continue;
                    await gateway.DeletePersonAsync(controller, userCode);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Falha ao revogar usuário {UserCode} no controlador {ControllerId}.", userCode, controllerId);
                }
            }
        });
    }

    private string? CurrentUsername() => User.Identity?.Name;
}
