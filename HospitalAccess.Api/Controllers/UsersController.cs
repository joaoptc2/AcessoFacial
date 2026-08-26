using HospitalAccess.Api.Services;
using HospitalAccess.Application.Sync;
using HospitalAccess.Domain.Entities;
using HospitalAccess.Domain.Enums;
using HospitalAccess.Gateway;
using HospitalAccess.Gateway.Imaging;
using HospitalAccess.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HospitalAccess.Api.Controllers;

public record CreateUserRequest(string Name, int TimeGroup, Guid? GroupId, Guid[]? ControllerIds = null, uint? CardNumber = null,
    string? Document = null, string? EmployeeId = null, string? JobTitle = null, string? Phone = null, string? Email = null, string? Notes = null);
public record UpdateUserRequest(string Name, int TimeGroup, Guid? GroupId, Guid[]? ControllerIds = null, uint? CardNumber = null,
    string? Document = null, string? EmployeeId = null, string? JobTitle = null, string? Phone = null, string? Email = null, string? Notes = null);

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
    private readonly GroupAccessService _groupAccess;
    private readonly IUserSyncQueue _syncQueue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<UsersController> _logger;

    public UsersController(AccessDbContext db, GroupAccessService groupAccess, IUserSyncQueue syncQueue, IServiceScopeFactory scopeFactory, ILogger<UsersController> logger)
    {
        _db = db;
        _groupAccess = groupAccess;
        _syncQueue = syncQueue;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>
    /// Lista os usuários permanentes PAGINADOS (visitantes ficam em /api/visitors). Busca por
    /// nome (case-insensitive) ou código exato; filtro opcional por grupo. Resposta no padrão
    /// {total, page, pageSize, items}.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] int page = 1, [FromQuery] int pageSize = 25,
        [FromQuery] string? search = null, [FromQuery] Guid? groupId = null,
        CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);

        var query = _db.Users.Where(u => u.Type == UserType.Permanent);
        if (!string.IsNullOrWhiteSpace(search))
        {
            // ToLower().Contains traduz para strpos(lower(...)) no Npgsql — sem curinga de LIKE
            // para escapar. Termo todo numérico também casa o código exato do usuário.
            var term = search.Trim().ToLowerInvariant();
            query = uint.TryParse(term, out var code)
                ? query.Where(u => u.Name.ToLower().Contains(term) || u.UserCode == code)
                : query.Where(u => u.Name.ToLower().Contains(term));
        }
        if (groupId is not null)
            query = query.Where(u => u.GroupId == groupId);

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderBy(u => u.Name).ThenBy(u => u.Id) // desempate estável entre páginas
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(u => new
            {
                u.Id,
                u.UserCode,
                u.Name,
                u.TimeGroup,
                u.GroupId,
                GroupName = u.Group != null ? u.Group.Name : null,
                u.CardNumber,
                u.JobTitle,
                HasFacePhoto = u.FacePhoto != null,
                u.CreatedByUsername,
                u.CreatedAtUtc,
                u.RevokedAtUtc,
                Controllers = u.Permissions.Select(p => new { p.ControllerId, ControllerName = p.Controller!.Name }),
            })
            .ToListAsync(ct);

        return Ok(new { total, page, pageSize, items });
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var user = await _db.Users
            .Include(u => u.Group)
            .Include(u => u.Permissions).ThenInclude(p => p.Controller)
            .FirstOrDefaultAsync(u => u.Id == id && u.Type == UserType.Permanent, ct);
        if (user is null) return NotFound();

        return Ok(new
        {
            user.Id,
            user.UserCode,
            user.Name,
            user.TimeGroup,
            user.GroupId,
            GroupName = user.Group?.Name,
            user.CardNumber,
            user.Document,
            user.EmployeeId,
            user.JobTitle,
            user.Phone,
            user.Email,
            user.Notes,
            HasFacePhoto = user.FacePhoto != null,
            user.CreatedByUsername,
            user.CreatedAtUtc,
            user.RevokedByUsername,
            user.RevokedAtUtc,
            ControllerIds = user.Permissions.Select(p => p.ControllerId),
            Controllers = user.Permissions
                .Select(p => new { p.ControllerId, ControllerName = p.Controller!.Name, GrantedByGroup = p.GrantedByGroupId != null })
                .OrderBy(c => c.ControllerName),
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
        if (!PersonNameRules.TryNormalize(request.Name, "O nome", out var name, out var nameError))
            return BadRequest(nameError);
        if (request.TimeGroup is < 1 or > 64)
            return BadRequest("TimeGroup deve estar entre 1 e 64.");
        if (request.GroupId is { } groupId && !await _db.UserGroups.AnyAsync(g => g.Id == groupId, ct))
            return BadRequest("Grupo não existe.");

        await using var ms = new MemoryStream();
        await facePhoto.CopyToAsync(ms, ct);

        // Valida a foto AQUI, não na sincronização: antes, um arquivo que nem era imagem era
        // gravado no banco, entrava na fila e só falhava no aparelho minutos depois — com uma
        // mensagem técnica longe de quem cadastrou.
        if (!FacePhotoValidator.TryValidate(ms.ToArray(), out var photoError))
            return BadRequest(photoError);

        var nextCode = await NextUserCodeAsync(ct);

        var user = new User
        {
            UserCode = nextCode,
            Name = name,
            Type = UserType.Permanent,
            TimeGroup = request.TimeGroup,
            GroupId = request.GroupId,
            CardNumber = request.CardNumber,
            Document = NullIfBlank(request.Document),
            EmployeeId = NullIfBlank(request.EmployeeId),
            JobTitle = NullIfBlank(request.JobTitle),
            Phone = NullIfBlank(request.Phone),
            Email = NullIfBlank(request.Email),
            Notes = NullIfBlank(request.Notes),
            FacePhoto = ms.ToArray(),
            CreatedByUsername = CurrentUsername(),
        };

        foreach (var controllerId in (request.ControllerIds ?? []).Distinct())
        {
            if (!await _db.Controllers.AnyAsync(c => c.Id == controllerId, ct))
                return BadRequest($"Controlador {controllerId} não existe.");
        }

        _db.Users.Add(user);
        // Portas efetivas = manuais pedidas ∪ portas herdadas do grupo (o serviço marca a origem).
        await _groupAccess.ReconcileUserPermissionsAsync(user, request.ControllerIds ?? [], request.TimeGroup, ct);
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

        if (!PersonNameRules.TryNormalize(request.Name, "O nome", out var name, out var nameError))
            return BadRequest(nameError);
        if (request.TimeGroup is < 1 or > 64)
            return BadRequest("TimeGroup deve estar entre 1 e 64.");
        if (request.GroupId is { } groupId && !await _db.UserGroups.AnyAsync(g => g.Id == groupId, ct))
            return BadRequest("Grupo não existe.");

        // Antes de aplicar a edição: algum campo que vai AO DISPOSITIVO mudou? Editar só perfil
        // administrativo (telefone, e-mail, cargo, notas...) não pode custar re-upload de face.
        // Compara o nome JÁ NORMALIZADO: senão, salvar sem mexer em nada dispara re-upload de
        // face só porque o valor gravado perdeu um espaço duplicado que veio no formulário.
        var hasNewPhoto = facePhoto is { Length: > 0 };
        var deviceFieldsChanged = UserDeviceFields.Changed(
            user, name, request.TimeGroup, request.CardNumber, hasNewPhoto);

        user.Name = name;
        user.TimeGroup = request.TimeGroup;
        user.GroupId = request.GroupId;
        user.CardNumber = request.CardNumber;
        user.Document = NullIfBlank(request.Document);
        user.EmployeeId = NullIfBlank(request.EmployeeId);
        user.JobTitle = NullIfBlank(request.JobTitle);
        user.Phone = NullIfBlank(request.Phone);
        user.Email = NullIfBlank(request.Email);
        user.Notes = NullIfBlank(request.Notes);

        if (facePhoto is { Length: > 0 })
        {
            await using var ms = new MemoryStream();
            await facePhoto.CopyToAsync(ms, ct);
            var newPhoto = ms.ToArray();
            if (!FacePhotoValidator.TryValidate(newPhoto, out var photoError))
                return BadRequest(photoError);
            user.FacePhoto = newPhoto;
        }

        var desiredControllerIds = (request.ControllerIds ?? []).Distinct().ToHashSet();
        foreach (var controllerId in desiredControllerIds)
        {
            if (!await _db.Controllers.AnyAsync(c => c.Id == controllerId, ct))
                return BadRequest($"Controlador {controllerId} não existe.");
        }

        // Conjunto efetivo = portas manuais desejadas ∪ portas herdadas do grupo (sempre presentes).
        var groupDoors = await _groupAccess.GetGroupDoorsAsync(user.GroupId, ct);
        var effectiveIds = new HashSet<Guid>(desiredControllerIds);
        effectiveIds.UnionWith(groupDoors);

        var existingControllerIds = user.Permissions.Select(p => p.ControllerId).ToHashSet();
        var addedControllerIds = effectiveIds.Where(cId => !existingControllerIds.Contains(cId)).ToList();
        var removedControllerIds = existingControllerIds.Where(cId => !effectiveIds.Contains(cId)).ToList();

        // Só quando um campo de DISPOSITIVO mudou, as portas que PERMANECEM voltam a 'Pending'
        // (força re-push de foto/nome/TimeGroup/cartão alterados). As portas REMOVIDAS ficam
        // 'Synced' de propósito: assim SyncUserAsync as revoga no hardware — se fossem zeradas
        // junto, a pessoa continuaria cadastrada na porta removida (a revogação só acontece a
        // partir de um status 'Synced').
        var syncStatuses = await _db.SyncStatuses.Where(s => s.UserId == id).ToListAsync(ct);
        if (deviceFieldsChanged)
        {
            foreach (var status in syncStatuses.Where(s => s.State == SyncState.Synced && effectiveIds.Contains(s.ControllerId)))
            {
                status.State = SyncState.Pending;
                status.UpdatedAt = DateTime.UtcNow;
            }
        }

        // Foto nova destrava TODAS as falhas deste usuário, inclusive quarentena e conflito de
        // duplicidade — uma foto diferente pode resolver "sem rosto" e "face duplicada".
        if (hasNewPhoto)
        {
            foreach (var status in syncStatuses.Where(s => s.State == SyncState.Failed))
            {
                status.State = SyncState.Pending;
                status.RetryCount = 0;
                status.NextRetryAtUtc = null;
                status.ConflictUserCode = null;
                status.UpdatedAt = DateTime.UtcNow;
            }
        }

        // Reconcilia as permissões (portas do grupo herdadas + extras manuais) marcando a origem.
        await _groupAccess.ReconcileUserPermissionsAsync(user, desiredControllerIds, request.TimeGroup, ct);

        UserAuditLogger.Record(_db, user, "Atualizado", CurrentUsername(),
            await BuildControllerChangeDetailsAsync(addedControllerIds, removedControllerIds, ct));

        await _db.SaveChangesAsync(ct);

        // Enfileirar sync só quando há trabalho de hardware: campo de dispositivo alterado ou
        // porta adicionada/removida. Edição puramente administrativa = zero tráfego.
        if (deviceFieldsChanged || addedControllerIds.Count > 0 || removedControllerIds.Count > 0)
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

        // Reativar é ação manual: destrava backoff/quarentena (menos conflitos de duplicidade).
        await ResetFailedForRetryAsync(
            _db.SyncStatuses.Where(s => s.UserId == id && s.ConflictUserCode == null), ct);
        SyncInBackground(id);

        return NoContent();
    }

    /// <summary>Reenfileira para sincronização TODOS os usuários com status de erro ou pendente (botão "Sincronizar com erro").</summary>
    [HttpPost("sync-failed")]
    [Authorize(Roles = "Admin,Operator")]
    public async Task<IActionResult> SyncFailed(CancellationToken ct)
    {
        // Ação manual destrava o backoff e a quarentena de falha permanente — exceto conflitos
        // de duplicidade (ConflictUserCode), que têm fluxo próprio de resolução (substituir/manter):
        // re-tentá-los sem resolver é upload garantido de ser rejeitado.
        await ResetFailedForRetryAsync(_db.SyncStatuses.Where(s => s.ConflictUserCode == null), ct);

        var userIds = await _db.SyncStatuses
            .Where(s => s.State == SyncState.Pending)
            .Select(s => s.UserId)
            .Distinct()
            .ToListAsync(ct);

        var enqueued = _syncQueue.EnqueueMany(userIds);
        return Ok(new { enqueued });
    }

    /// <summary>Reenfileira um usuário específico para sincronização com seus controladores.</summary>
    [HttpPost("{id:guid}/resync")]
    [Authorize(Roles = "Admin,Operator")]
    public async Task<IActionResult> Resync(Guid id, CancellationToken ct)
    {
        // Sem filtro de tipo: a tela de Sincronizações força visitantes também (portas de leito) —
        // o corpo (reset de backoff/quarentena + enqueue) é idêntico para os dois tipos.
        if (!await _db.Users.AnyAsync(u => u.Id == id, ct)) return NotFound();
        await ResetFailedForRetryAsync(
            _db.SyncStatuses.Where(s => s.UserId == id && s.ConflictUserCode == null), ct);
        _syncQueue.EnqueueSync(id);
        return Accepted(new { message = "Sincronização reenfileirada." });
    }

    /// <summary>
    /// Volta status Failed para Pending zerando o backoff/quarentena (<see cref="SyncRetryPolicy"/>) —
    /// usado pelas ações manuais de resync, que têm precedência sobre a espera automática.
    /// </summary>
    private static Task<int> ResetFailedForRetryAsync(IQueryable<DeviceSyncStatus> failedScope, CancellationToken ct) =>
        failedScope.Where(s => s.State == SyncState.Failed)
            .ExecuteUpdateAsync(u => u
                .SetProperty(s => s.State, SyncState.Pending)
                .SetProperty(s => s.RetryCount, 0)
                .SetProperty(s => s.NextRetryAtUtc, (DateTime?)null)
                .SetProperty(s => s.UpdatedAt, DateTime.UtcNow), ct);

    /// <summary>
    /// Resolve um conflito de face duplicada em um controlador SUBSTITUINDO: exclui o usuário
    /// existente que colidiu e reenvia este usuário. Roda em segundo plano.
    /// </summary>
    [HttpPost("{id:guid}/sync/{controllerId:guid}/replace")]
    [Authorize(Roles = "Admin,Operator")]
    public async Task<IActionResult> ResolveConflictReplace(Guid id, Guid controllerId, CancellationToken ct)
    {
        if (!await _db.Users.AnyAsync(u => u.Id == id, ct)) return NotFound();
        ResolveConflictInBackground(sync => sync.ReplaceConflictAsync(id, controllerId), "substituir conflito");
        return Accepted(new { message = "Substituição iniciada." });
    }

    /// <summary>
    /// Resolve um conflito de face duplicada MANTENDO o existente: cancela o envio deste usuário
    /// para o controlador (remove a permissão dele naquela porta).
    /// </summary>
    [HttpPost("{id:guid}/sync/{controllerId:guid}/keep-existing")]
    [Authorize(Roles = "Admin,Operator")]
    public async Task<IActionResult> ResolveConflictKeepExisting(Guid id, Guid controllerId, CancellationToken ct)
    {
        if (!await _db.Users.AnyAsync(u => u.Id == id, ct)) return NotFound();
        var scope = _scopeFactory.CreateScope();
        try
        {
            var sync = scope.ServiceProvider.GetRequiredService<IUserSyncService>();
            await sync.KeepExistingOnConflictAsync(id, controllerId, ct);
        }
        finally { scope.Dispose(); }
        return NoContent();
    }

    private void ResolveConflictInBackground(Func<IUserSyncService, Task> action, string description)
    {
        _ = Task.Run(async () =>
        {
            using var scope = _scopeFactory.CreateScope();
            var sync = scope.ServiceProvider.GetRequiredService<IUserSyncService>();
            try { await action(sync); }
            catch (Exception ex) { _logger.LogError(ex, "Falha ao {Description} em segundo plano.", description); }
        });
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

    /// <summary>Foto de face do usuário (JPEG). 404 se não houver. Cacheável por 5 min.</summary>
    [HttpGet("{id:guid}/photo")]
    public async Task<IActionResult> Photo(Guid id, CancellationToken ct)
    {
        var photo = await _db.Users
            .Where(u => u.Id == id)
            .Select(u => u.FacePhoto)
            .FirstOrDefaultAsync(ct);
        if (photo is null || photo.Length == 0) return NotFound();

        Response.Headers.CacheControl = "private, max-age=300";
        return File(photo, "image/jpeg");
    }

    /// <summary>
    /// Relatório de portas que o usuário acessou: eventos do AccessLog (por UserCode) num período,
    /// com um resumo agrupado por porta (quantas vezes, concedidos, última vez) e os eventos recentes.
    /// </summary>
    [HttpGet("{id:guid}/access-log")]
    public async Task<IActionResult> AccessHistory(
        Guid id, [FromQuery] DateTime? from, [FromQuery] DateTime? to, [FromQuery] int take = 100, CancellationToken ct = default)
    {
        var userCode = await _db.Users
            .Where(u => u.Id == id && u.Type == UserType.Permanent)
            .Select(u => (uint?)u.UserCode)
            .FirstOrDefaultAsync(ct);
        if (userCode is null) return NotFound();

        take = Math.Clamp(take, 1, 500);
        var fromUtc = ToUtc(from);
        var toUtc = ToUtc(to);

        var query = _db.AccessLogs.AsNoTracking().Where(l => l.UserCode == userCode);
        if (fromUtc is not null) query = query.Where(l => l.TimestampUtc >= fromUtc);
        if (toUtc is not null) query = query.Where(l => l.TimestampUtc <= toUtc);

        var total = await query.CountAsync(ct);
        var granted = await query.CountAsync(l => l.Granted, ct);

        // Resumo por porta (o "relatório de portas"): quais portas, quantas vezes, última vez.
        var doors = await query
            .GroupBy(l => new { l.ControllerId, l.ControllerName })
            .Select(g => new
            {
                g.Key.ControllerId,
                g.Key.ControllerName,
                Total = g.Count(),
                Granted = g.Count(x => x.Granted),
                LastUtc = g.Max(x => x.TimestampUtc),
            })
            .OrderByDescending(d => d.LastUtc)
            .ToListAsync(ct);

        var items = await query
            .OrderByDescending(l => l.TimestampUtc)
            .Take(take)
            .Select(l => new { l.TimestampUtc, l.ControllerName, l.Method, l.Direction, l.Granted })
            .ToListAsync(ct);

        return Ok(new
        {
            userCode,
            total,
            granted,
            denied = total - granted,
            distinctDoors = doors.Count,
            lastAccessUtc = doors.Count > 0 ? doors.Max(d => d.LastUtc) : (DateTime?)null,
            doors,
            items,
        });
    }

    private static DateTime? ToUtc(DateTime? value) => value?.Kind switch
    {
        null => null,
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.Value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value!.Value, DateTimeKind.Utc),
    };

    // Sincronização fora do ciclo da requisição: os comandos ao hardware são TCP com retries e podem
    // levar minutos — não devem bloquear a resposta HTTP. Vão para a FILA SERIAL (UserSyncQueue), que
    // processa um usuário de cada vez, evitando o CommandStatus_Timeout de vários AddPersonAndImage
    // concorrentes no mesmo controlador. Progresso fica em DeviceSyncStatus (Pending/Synced/Failed).
    private void SyncInBackground(Guid userId) => _syncQueue.EnqueueSync(userId);

    private void RevokeInBackground(Guid userId) => _syncQueue.EnqueueRevoke(userId);

    /// <summary>Revoga (na fila) um usuário já excluído do banco, por código + controladores capturados antes do delete.</summary>
    private void RevokeDeletedUserInBackground(uint userCode, List<Guid> controllerIds) =>
        _syncQueue.EnqueueRevokeDeleted(userCode, controllerIds);

    private string? CurrentUsername() => User.Identity?.Name;

    /// <summary>Normaliza string de formulário vazia/em-branco para null (campos de perfil opcionais).</summary>
    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

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
