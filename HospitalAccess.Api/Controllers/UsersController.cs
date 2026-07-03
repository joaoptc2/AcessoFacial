using HospitalAccess.Api.Services;
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

public record CreateUserRequest(string Name, int TimeGroup, Guid? GroupId, Guid[]? ControllerIds = null, uint? CardNumber = null);
public record UpdateUserRequest(string Name, int TimeGroup, Guid? GroupId, Guid[]? ControllerIds = null, uint? CardNumber = null);

/// <summary>
/// Cadastro/gestão de usuários permanentes (acesso por face) e disparo de sincronização.
/// Reception tem acesso só de leitura (List/Get/histórico) — todo endpoint de escrita tem
/// seu próprio [Authorize(Roles = "Admin,Operator")], que combinado com o da classe (AND)
/// restringe a escrita a Admin/Operator mesmo Reception estando no nível da classe.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = "Admin,Operator,Reception")]
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
                u.CardNumber,
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
            user.CardNumber,
            HasFacePhoto = user.FacePhoto != null,
            ControllerIds = user.Permissions.Select(p => p.ControllerId),
        });
    }

    /// <summary>Cria usuário com foto (JPG). A foto é convertida para 480x640/<=120KB no gateway.</summary>
    [HttpPost]
    [Authorize(Roles = "Admin,Operator")]
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

        var nextCode = await NextUserCodeAsync(ct);

        var user = new User
        {
            UserCode = nextCode,
            Name = request.Name,
            Type = UserType.Permanent,
            TimeGroup = request.TimeGroup,
            GroupId = request.GroupId,
            CardNumber = request.CardNumber,
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
        UserAuditLogger.Record(_db, user, "Criado", CurrentUsername());
        await _db.SaveChangesAsync(ct);

        SyncInBackground(user.Id);

        return CreatedAtAction(nameof(Get), new { id = user.Id }, new { user.Id, user.UserCode });
    }

    /// <summary>Atualiza nome, grupo de horário, grupo organizacional, portas e (opcionalmente) a foto.</summary>
    [HttpPut("{id:guid}")]
    [Authorize(Roles = "Admin,Operator")]
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
        user.CardNumber = request.CardNumber;

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

        // Edição altera dados que o dispositivo já tem (foto/nome/TimeGroup) ou as portas: os
        // status já 'Synced' precisam voltar a 'Pending', senão SyncUserAsync os pula e a
        // alteração nunca chega ao hardware.
        var syncStatuses = await _db.SyncStatuses.Where(s => s.UserId == id).ToListAsync(ct);
        foreach (var status in syncStatuses.Where(s => s.State == SyncState.Synced))
        {
            status.State = SyncState.Pending;
            status.UpdatedAt = DateTime.UtcNow;
        }

        // Remove permissões que não estão mais na lista desejada; adiciona as novas.
        var existingControllerIds = user.Permissions.Select(p => p.ControllerId).ToHashSet();
        var addedControllerIds = desiredControllerIds.Where(cId => !existingControllerIds.Contains(cId)).ToList();
        var removedControllerIds = existingControllerIds.Where(cId => !desiredControllerIds.Contains(cId)).ToList();

        var toRemove = user.Permissions.Where(p => !desiredControllerIds.Contains(p.ControllerId)).ToList();
        foreach (var p in toRemove) _db.Permissions.Remove(p);

        foreach (var controllerId in addedControllerIds)
        {
            // _db.Add (não user.Permissions.Add): como AccessPermission.Id já vem preenchido
            // (Guid.NewGuid() no inicializador), o EF Core marca entidades adicionadas via fixup
            // de uma coleção navigation já rastreada como Modified em vez de Added — gera UPDATE
            // em vez de INSERT e lança DbUpdateConcurrencyException (0 linhas afetadas). DbSet.Add
            // força o estado Added independente do valor da chave.
            _db.Permissions.Add(new AccessPermission { UserId = user.Id, ControllerId = controllerId, TimeGroup = request.TimeGroup });
        }
        foreach (var p in user.Permissions.Where(p => desiredControllerIds.Contains(p.ControllerId)))
        {
            p.TimeGroup = request.TimeGroup;
        }

        UserAuditLogger.Record(_db, user, "Atualizado", CurrentUsername(),
            await BuildControllerChangeDetailsAsync(addedControllerIds, removedControllerIds, ct));

        await _db.SaveChangesAsync(ct);
        SyncInBackground(user.Id);

        return NoContent();
    }

    private async Task<string?> BuildControllerChangeDetailsAsync(List<Guid> added, List<Guid> removed, CancellationToken ct)
    {
        if (added.Count == 0 && removed.Count == 0) return null;

        var names = await _db.Controllers
            .Where(c => added.Contains(c.Id) || removed.Contains(c.Id))
            .Select(c => new { c.Id, c.Name })
            .ToDictionaryAsync(c => c.Id, c => c.Name, ct);

        var parts = added.Select(id => $"+{names.GetValueOrDefault(id, "?")}")
            .Concat(removed.Select(id => $"-{names.GetValueOrDefault(id, "?")}"));
        return "Portas: " + string.Join(", ", parts);
    }

    /// <summary>Remove o usuário: apaga o cadastro e revoga (em segundo plano) nos controladores. O log de acessos já gravado não é afetado (guarda nome/código como snapshot).</summary>
    [HttpDelete("{id:guid}")]
    [Authorize(Roles = "Admin,Operator")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == id && u.Type == UserType.Permanent, ct);
        if (user is null) return NotFound();

        // Captura o necessário para revogar no hardware antes de apagar a linha (e o
        // DeviceSyncStatus em cascata), já que o RevokeUserAsync(userId) depende dessas
        // linhas ainda existirem. Usa a UNIÃO das portas com permissão atual E das portas onde
        // há qualquer status de sincronização não revogado: senão, uma porta com sync
        // pendente/falha (que não está mais na lista de permissões) ficaria com a credencial
        // ativa no hardware para sempre.
        var userCode = user.UserCode;
        var permissionControllerIds = await _db.Permissions
            .Where(p => p.UserId == id)
            .Select(p => p.ControllerId)
            .ToListAsync(ct);
        var syncedControllerIds = await _db.SyncStatuses
            .Where(s => s.UserId == id && s.State != SyncState.Revoked)
            .Select(s => s.ControllerId)
            .ToListAsync(ct);
        var controllerIds = permissionControllerIds.Union(syncedControllerIds).Distinct().ToList();

        UserAuditLogger.Record(_db, user, "Excluído", CurrentUsername());
        _db.Users.Remove(user);
        await _db.SaveChangesAsync(ct);

        RevokeDeletedUserInBackground(userCode, controllerIds);

        return NoContent();
    }

    /// <summary>Revoga o acesso sem apagar o cadastro (ao contrário de Delete): bloqueia a sincronização e remove a pessoa dos controladores, mas o registro pode ser reativado depois.</summary>
    [HttpPost("{id:guid}/revoke")]
    [Authorize(Roles = "Admin,Operator")]
    public async Task<IActionResult> Revoke(Guid id, CancellationToken ct)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == id && u.Type == UserType.Permanent, ct);
        if (user is null) return NotFound();
        if (user.RevokedAtUtc is not null) return Conflict("Usuário já está revogado.");

        user.RevokedAtUtc = DateTime.UtcNow;
        user.RevokedByUsername = CurrentUsername();
        UserAuditLogger.Record(_db, user, "Revogado", CurrentUsername());
        await _db.SaveChangesAsync(ct);

        RevokeInBackground(id);

        return NoContent();
    }

    /// <summary>Reverte uma revogação: o usuário volta a ser sincronizado normalmente nos controladores das portas já cadastradas.</summary>
    [HttpPost("{id:guid}/reactivate")]
    [Authorize(Roles = "Admin,Operator")]
    public async Task<IActionResult> Reactivate(Guid id, CancellationToken ct)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == id && u.Type == UserType.Permanent, ct);
        if (user is null) return NotFound();
        if (user.RevokedAtUtc is null) return Conflict("Usuário não está revogado.");

        user.RevokedAtUtc = null;
        user.RevokedByUsername = null;
        UserAuditLogger.Record(_db, user, "Reativado", CurrentUsername());
        await _db.SaveChangesAsync(ct);

        SyncInBackground(id);

        return NoContent();
    }

    /// <summary>Histórico administrativo do usuário (criação, edições, revogação/reativação, exclusão). Sobrevive à exclusão do cadastro.</summary>
    [HttpGet("{id:guid}/audit-log")]
    public async Task<IActionResult> AuditLog(Guid id, CancellationToken ct)
    {
        var entries = await _db.UserAuditLogs
            .Where(a => a.UserId == id)
            .OrderByDescending(a => a.TimestampUtc)
            .Select(a => new { a.TimestampUtc, a.Action, a.PerformedByUsername, a.Details })
            .ToListAsync(ct);
        return Ok(entries);
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

    /// <summary>Revoga (sem excluir) um usuário ainda cadastrado no banco, via IUserSyncService — mesmo caminho usado para visitantes.</summary>
    private void RevokeInBackground(Guid userId)
    {
        _ = Task.Run(async () =>
        {
            using var scope = _scopeFactory.CreateScope();
            var sync = scope.ServiceProvider.GetRequiredService<IUserSyncService>();
            try
            {
                await sync.RevokeUserAsync(userId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha ao revogar usuário {UserId} em segundo plano.", userId);
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

    /// <summary>
    /// Próximo UserCode via sequence do PostgreSQL: atômico entre requisições concorrentes e
    /// monotônico (nunca reusa código de usuário excluído, o que corromperia o histórico).
    /// </summary>
    private async Task<uint> NextUserCodeAsync(CancellationToken ct)
    {
        // SqlQueryRaw (não SqlQuery interpolado): o nome da sequence é uma constante do sistema,
        // deve ser embutido como literal e não parametrizado (nextval não aceita parâmetro no nome).
        var next = await _db.Database
            .SqlQueryRaw<long>($"SELECT nextval('{AccessDbContext.UserCodeSequence}') AS \"Value\"")
            .SingleAsync(ct);
        return (uint)next;
    }
}
