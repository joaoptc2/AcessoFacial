using HospitalAccess.Api.Services;
using HospitalAccess.Application.Sync;
using HospitalAccess.Domain.Entities;
using HospitalAccess.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HospitalAccess.Api.Controllers;

public record UserGroupRequest(string Name, string? Description);
public record UpdateGroupDefaultControllersRequest(Guid[] ControllerIds);

/// <summary>Grupos organizacionais de usuários (ex.: "Enfermagem", "Manutenção"). As portas padrão
/// do grupo são propagadas (herdadas) para todos os membros — ver GroupAccessService.</summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = "Admin,Operator")]
public class UserGroupsController : ControllerBase
{
    private readonly AccessDbContext _db;
    private readonly GroupAccessService _groupAccess;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<UserGroupsController> _logger;

    public UserGroupsController(AccessDbContext db, GroupAccessService groupAccess, IServiceScopeFactory scopeFactory, ILogger<UserGroupsController> logger)
    {
        _db = db;
        _groupAccess = groupAccess;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var groups = await _db.UserGroups
            .OrderBy(g => g.Name)
            .Select(g => new
            {
                g.Id,
                g.Name,
                g.Description,
                UserCount = g.Users.Count,
                DefaultControllerIds = g.DefaultControllers.Select(d => d.ControllerId),
            })
            .ToListAsync(ct);
        return Ok(groups);
    }

    /// <summary>Portas que usuários deste grupo recebem por padrão ao serem associados a ele (ver GroupControllerDefault).</summary>
    [HttpPut("{id:guid}/default-controllers")]
    public async Task<IActionResult> UpdateDefaultControllers(Guid id, [FromBody] UpdateGroupDefaultControllersRequest request, CancellationToken ct)
    {
        var group = await _db.UserGroups
            .Include(g => g.DefaultControllers)
            .FirstOrDefaultAsync(g => g.Id == id, ct);
        if (group is null) return NotFound();

        var desiredIds = (request.ControllerIds ?? []).Distinct().ToHashSet();
        foreach (var controllerId in desiredIds)
        {
            if (!await _db.Controllers.AnyAsync(c => c.Id == controllerId, ct))
                return BadRequest($"Controlador {controllerId} não existe.");
        }

        var existingIds = group.DefaultControllers.Select(d => d.ControllerId).ToHashSet();
        var removedControllerIds = existingIds.Where(cId => !desiredIds.Contains(cId)).ToList();
        var addedControllerIds = desiredIds.Where(cId => !existingIds.Contains(cId)).ToList();

        foreach (var d in group.DefaultControllers.Where(d => removedControllerIds.Contains(d.ControllerId)).ToList())
            _db.GroupControllerDefaults.Remove(d);

        foreach (var controllerId in addedControllerIds)
        {
            // _db.Add (não group.DefaultControllers.Add): como GroupControllerDefault.Id já vem
            // preenchido (Guid.NewGuid() no inicializador), o EF Core marca entidades adicionadas
            // via fixup de uma coleção navigation já rastreada como Modified em vez de Added — gera
            // UPDATE em vez de INSERT e lança DbUpdateConcurrencyException (0 linhas afetadas).
            // DbSet.Add força o estado Added independente do valor da chave.
            _db.GroupControllerDefaults.Add(new GroupControllerDefault { GroupId = group.Id, ControllerId = controllerId });
        }

        // Propaga a mudança para todos os membros do grupo (herdadas): adiciona/remove a porta em
        // cada um e sincroniza no hardware quem realmente mudou.
        var affected = await _groupAccess.PropagateGroupDoorsAsync(group.Id, addedControllerIds, removedControllerIds, ct);

        await _db.SaveChangesAsync(ct);
        SyncUsersInBackground(affected);
        return NoContent();
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] UserGroupRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name)) return BadRequest("Name é obrigatório.");

        var group = new UserGroup { Name = request.Name, Description = request.Description };
        _db.UserGroups.Add(group);
        await _db.SaveChangesAsync(ct);

        return CreatedAtAction(nameof(List), new { id = group.Id }, new { group.Id });
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UserGroupRequest request, CancellationToken ct)
    {
        var group = await _db.UserGroups.FirstOrDefaultAsync(g => g.Id == id, ct);
        if (group is null) return NotFound();
        if (string.IsNullOrWhiteSpace(request.Name)) return BadRequest("Name é obrigatório.");

        group.Name = request.Name;
        group.Description = request.Description;
        await _db.SaveChangesAsync(ct);

        return NoContent();
    }

    /// <summary>Remove o grupo. Usuários do grupo NÃO são apagados: ficam sem grupo e perdem as
    /// portas que eram herdadas dele (as portas manuais permanecem); o hardware é reconciliado.</summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var group = await _db.UserGroups.FirstOrDefaultAsync(g => g.Id == id, ct);
        if (group is null) return NotFound();

        // Remove as permissões herdadas deste grupo dos membros ANTES de apagá-lo (com o grupo
        // apagado, User.GroupId vira null por cascade SetNull e não dá mais para saber quem era membro).
        var affected = await _groupAccess.RemoveGroupFromMembersAsync(id, ct);

        _db.UserGroups.Remove(group);
        await _db.SaveChangesAsync(ct);

        // Sincroniza os membros afetados (revoga no hardware as portas que eram só do grupo).
        SyncUsersInBackground(affected);
        return NoContent();
    }

    /// <summary>
    /// Sincroniza vários usuários com o hardware fora do ciclo da requisição (mesmo padrão
    /// fire-and-forget do UsersController): comandos TCP com retry podem levar minutos e não devem
    /// bloquear a resposta. Progresso fica em DeviceSyncStatus, reprocessado por RetryPendingAsync.
    /// </summary>
    private void SyncUsersInBackground(IReadOnlyCollection<Guid> userIds)
    {
        if (userIds.Count == 0) return;

        _ = Task.Run(async () =>
        {
            using var scope = _scopeFactory.CreateScope();
            var sync = scope.ServiceProvider.GetRequiredService<IUserSyncService>();
            foreach (var userId in userIds)
            {
                try { await sync.SyncUserAsync(userId); }
                catch (Exception ex) { _logger.LogError(ex, "Falha ao sincronizar usuário {UserId} após mudança de grupo.", userId); }
            }
        });
    }
}
