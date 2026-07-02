using HospitalAccess.Domain.Entities;
using HospitalAccess.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HospitalAccess.Api.Controllers;

public record UserGroupRequest(string Name, string? Description);
public record UpdateGroupDefaultControllersRequest(Guid[] ControllerIds);

/// <summary>Grupos organizacionais de usuários (ex.: "Enfermagem", "Manutenção") — só para organização/UI.</summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = "Admin,Operator")]
public class UserGroupsController : ControllerBase
{
    private readonly AccessDbContext _db;

    public UserGroupsController(AccessDbContext db)
    {
        _db = db;
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

        var toRemove = group.DefaultControllers.Where(d => !desiredIds.Contains(d.ControllerId)).ToList();
        foreach (var d in toRemove) _db.GroupControllerDefaults.Remove(d);

        var existingIds = group.DefaultControllers.Select(d => d.ControllerId).ToHashSet();
        foreach (var controllerId in desiredIds.Where(cId => !existingIds.Contains(cId)))
        {
            // _db.Add (não group.DefaultControllers.Add): como GroupControllerDefault.Id já vem
            // preenchido (Guid.NewGuid() no inicializador), o EF Core marca entidades adicionadas
            // via fixup de uma coleção navigation já rastreada como Modified em vez de Added — gera
            // UPDATE em vez de INSERT e lança DbUpdateConcurrencyException (0 linhas afetadas).
            // DbSet.Add força o estado Added independente do valor da chave.
            _db.GroupControllerDefaults.Add(new GroupControllerDefault { GroupId = group.Id, ControllerId = controllerId });
        }

        await _db.SaveChangesAsync(ct);
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

    /// <summary>Remove o grupo. Usuários do grupo NÃO são apagados, apenas ficam sem grupo.</summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var group = await _db.UserGroups.FirstOrDefaultAsync(g => g.Id == id, ct);
        if (group is null) return NotFound();

        _db.UserGroups.Remove(group);
        await _db.SaveChangesAsync(ct);

        return NoContent();
    }
}
