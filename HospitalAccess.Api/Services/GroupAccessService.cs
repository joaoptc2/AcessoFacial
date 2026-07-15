using HospitalAccess.Domain.Entities;
using HospitalAccess.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HospitalAccess.Api.Services;

/// <summary>
/// Mantém as permissões de porta em sincronia com o grupo organizacional do usuário. Modelo
/// "base + extras manuais": as portas padrão do grupo (GroupControllerDefault) são HERDADAS por
/// todos os membros (AccessPermission.GrantedByGroupId = grupo) e o operador pode adicionar portas
/// extras individuais (GrantedByGroupId = null). Mudar as portas do grupo, ou o usuário de grupo,
/// propaga só as herdadas; as manuais permanecem.
///
/// Todos os métodos mutam o contexto rastreado; quem chama dá o <c>SaveChanges</c> e dispara o
/// sync com o hardware (IUserSyncService). O sync é idempotente: ao remover a AccessPermission de
/// uma porta cujo DeviceSyncStatus ainda é <c>Synced</c>, o SyncUserAsync revoga a pessoa naquele
/// controlador — por isso a propagação NÃO mexe nos DeviceSyncStatus das portas removidas.
/// </summary>
public sealed class GroupAccessService
{
    private readonly AccessDbContext _db;

    public GroupAccessService(AccessDbContext db)
    {
        _db = db;
    }

    /// <summary>Portas padrão de um grupo (vazio se <paramref name="groupId"/> for null).</summary>
    public async Task<HashSet<Guid>> GetGroupDoorsAsync(Guid? groupId, CancellationToken ct)
    {
        if (groupId is null) return new HashSet<Guid>();
        var ids = await _db.GroupControllerDefaults
            .Where(d => d.GroupId == groupId)
            .Select(d => d.ControllerId)
            .ToListAsync(ct);
        return ids.ToHashSet();
    }

    /// <summary>
    /// Reconcilia as permissões de UM usuário (já rastreado, com <c>Permissions</c> incluído) para
    /// refletir: portas do grupo (herdadas, sempre presentes) ∪ portas extras manuais
    /// (<paramref name="desiredControllerIds"/> menos as do grupo). Não salva nem sincroniza.
    /// Usado ao criar/editar usuário (cobre também entrar/trocar/sair de grupo, pois usa o
    /// <c>user.GroupId</c> atual). Requer que os controladores já tenham sido validados pelo chamador.
    /// </summary>
    public async Task ReconcileUserPermissionsAsync(User user, IEnumerable<Guid> desiredControllerIds, int timeGroup, CancellationToken ct)
    {
        var groupDoors = await GetGroupDoorsAsync(user.GroupId, ct);
        var effective = ComputeEffectivePermissions(desiredControllerIds, groupDoors, user.GroupId);

        foreach (var p in user.Permissions.Where(p => !effective.ContainsKey(p.ControllerId)).ToList())
            _db.Permissions.Remove(p);

        foreach (var (controllerId, source) in effective)
        {
            var existing = user.Permissions.FirstOrDefault(p => p.ControllerId == controllerId);
            if (existing is null)
            {
                // _db.Permissions.Add (não user.Permissions.Add): a PK já vem preenchida
                // (Guid.NewGuid()); via coleção rastreada o EF marcaria como Modified → UPDATE de 0
                // linhas. DbSet.Add força o estado Added. (Mesmo motivo do UsersController.)
                _db.Permissions.Add(new AccessPermission
                {
                    UserId = user.Id,
                    ControllerId = controllerId,
                    TimeGroup = timeGroup,
                    GrantedByGroupId = source,
                });
            }
            else
            {
                existing.TimeGroup = timeGroup;
                existing.GrantedByGroupId = source;
            }
        }
    }

    /// <summary>
    /// Lógica pura (testável sem banco): conjunto efetivo de portas de um usuário = portas manuais
    /// desejadas ∪ portas do grupo (sempre presentes, donas do grupo). Devolve porta → origem
    /// (<paramref name="groupId"/> se herdada do grupo; <c>null</c> se manual).
    /// </summary>
    public static IReadOnlyDictionary<Guid, Guid?> ComputeEffectivePermissions(
        IEnumerable<Guid> desiredControllerIds, IReadOnlyCollection<Guid> groupDoors, Guid? groupId)
    {
        var result = new Dictionary<Guid, Guid?>();
        foreach (var id in desiredControllerIds)
            result[id] = groupDoors.Contains(id) ? groupId : null;
        // Portas do grupo entram por último: são donas do grupo mesmo se também vieram como manual.
        foreach (var id in groupDoors)
            result[id] = groupId;
        return result;
    }

    /// <summary>
    /// Propaga uma mudança nas portas padrão do grupo para TODOS os membros: adiciona as
    /// <paramref name="addedControllers"/> (herdadas) a quem não tem e remove as
    /// <paramref name="removedControllers"/> herdadas (deixa as manuais intactas). Não salva.
    /// Devolve os IDs dos usuários cujo conjunto de portas MUDOU no hardware (para sincronizar) —
    /// mudanças de mera etiqueta (porta já existia) não entram na lista.
    /// </summary>
    public async Task<List<Guid>> PropagateGroupDoorsAsync(
        Guid groupId, ICollection<Guid> addedControllers, ICollection<Guid> removedControllers, CancellationToken ct)
    {
        if (addedControllers.Count == 0 && removedControllers.Count == 0) return new List<Guid>();

        var members = await _db.Users
            .Include(u => u.Permissions)
            .Where(u => u.GroupId == groupId)
            .ToListAsync(ct);

        var affected = new List<Guid>();
        foreach (var user in members)
        {
            var existingByController = user.Permissions.ToDictionary(p => p.ControllerId, p => p.GrantedByGroupId);
            var plan = PlanMemberDoorChange(existingByController, addedControllers, removedControllers, groupId);

            foreach (var controllerId in plan.ToAddInherited)
            {
                _db.Permissions.Add(new AccessPermission
                {
                    UserId = user.Id,
                    ControllerId = controllerId,
                    TimeGroup = user.TimeGroup,
                    GrantedByGroupId = groupId,
                });
            }
            // Porta que o membro já tinha como manual passa a ser do grupo: só re-etiqueta (a porta
            // já está no hardware, não precisa re-sincronizar por isso).
            foreach (var controllerId in plan.ToRetagToGroup)
                user.Permissions.First(p => p.ControllerId == controllerId).GrantedByGroupId = groupId;
            foreach (var controllerId in plan.ToRemoveInherited)
                _db.Permissions.Remove(user.Permissions.First(p => p.ControllerId == controllerId));

            if (plan.HardwareChanged) affected.Add(user.Id);
        }
        return affected;
    }

    /// <summary>Efeito de uma mudança nas portas do grupo sobre UM membro (puro, testável sem banco).</summary>
    public readonly record struct MemberDoorPlan(
        IReadOnlyList<Guid> ToAddInherited, IReadOnlyList<Guid> ToRemoveInherited, IReadOnlyList<Guid> ToRetagToGroup, bool HardwareChanged);

    /// <summary>
    /// Decide, para um membro (dado o mapa porta→origem que ele já tem), o que fazer com as portas
    /// <paramref name="addedControllers"/>/<paramref name="removedControllers"/> do grupo: adicionar
    /// as que faltam (herdadas), re-etiquetar as que ele já tinha manualmente, e remover só as
    /// herdadas deste grupo (portas manuais — origem <c>null</c> — nunca são removidas).
    /// </summary>
    public static MemberDoorPlan PlanMemberDoorChange(
        IReadOnlyDictionary<Guid, Guid?> existingByController,
        IEnumerable<Guid> addedControllers, IEnumerable<Guid> removedControllers, Guid groupId)
    {
        var toAdd = new List<Guid>();
        var toRetag = new List<Guid>();
        var toRemove = new List<Guid>();

        foreach (var controllerId in addedControllers)
        {
            if (!existingByController.TryGetValue(controllerId, out var source)) toAdd.Add(controllerId);
            else if (source != groupId) toRetag.Add(controllerId);
        }
        foreach (var controllerId in removedControllers)
        {
            if (existingByController.TryGetValue(controllerId, out var source) && source == groupId)
                toRemove.Add(controllerId);
        }
        return new MemberDoorPlan(toAdd, toRemove, toRetag, toAdd.Count > 0 || toRemove.Count > 0);
    }

    /// <summary>
    /// Remove de TODOS os membros as portas herdadas de um grupo (usado ao excluir o grupo). Não
    /// salva. Devolve os IDs dos membros afetados (para revogar no hardware as portas que eram só
    /// do grupo). As portas manuais dos membros permanecem.
    /// </summary>
    public async Task<List<Guid>> RemoveGroupFromMembersAsync(Guid groupId, CancellationToken ct)
    {
        var members = await _db.Users
            .Include(u => u.Permissions)
            .Where(u => u.GroupId == groupId)
            .ToListAsync(ct);

        var affected = new List<Guid>();
        foreach (var user in members)
        {
            var inherited = user.Permissions.Where(p => p.GrantedByGroupId == groupId).ToList();
            if (inherited.Count == 0) continue;
            foreach (var p in inherited) _db.Permissions.Remove(p);
            affected.Add(user.Id);
        }
        return affected;
    }
}
