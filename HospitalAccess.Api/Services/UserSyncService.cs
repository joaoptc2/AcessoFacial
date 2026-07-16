using HospitalAccess.Api.Options;
using HospitalAccess.Application.Sync;
using HospitalAccess.Domain.Entities;
using HospitalAccess.Domain.Enums;
using HospitalAccess.Gateway;
using HospitalAccess.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HospitalAccess.Api.Services;

/// <summary>
/// Orquestra o cadastro/remoção de usuários nos controladores, com fila de retry por
/// dispositivo (DeviceSyncStatus). Aplica-se a AMBOS os tipos:
/// - Permanentes: cadastrados com face (AddPersonWithFace).
/// - Visitantes: cadastrados SEM face, só com código + validade nativa (Person.Expiry) e
///   grupo de horário (AddPersonWithoutFace). O leitor não diferencia visitante de permanente —
///   valida a pessoa cadastrada; o QR só carrega o código. A expiração/revogação é gerida pelo
///   sistema removendo a pessoa do controlador (ver IVisitorExpirationJob).
/// </summary>
public sealed class UserSyncService : IUserSyncService
{
    private readonly AccessDbContext _db;
    private readonly IDeviceGateway _gateway;
    private readonly SyncRetryOptions _retryOptions;
    private readonly ILogger<UserSyncService> _logger;

    public UserSyncService(AccessDbContext db, IDeviceGateway gateway,
        IOptions<SyncRetryOptions> retryOptions, ILogger<UserSyncService> logger)
    {
        _db = db;
        _gateway = gateway;
        _retryOptions = retryOptions.Value;
        _logger = logger;
    }

    public async Task SyncUserAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await _db.Users
            .Include(u => u.Permissions)
            .FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return;

        if (user.RevokedAtUtc is not null)
        {
            // Usuário revogado (mas não excluído): nunca (re)cadastrar nos controladores,
            // mesmo que uma permissão nova tenha sido adicionada nesse meio-tempo — só volta
            // a sincronizar depois de reativado (ver UsersController.Reactivate).
            return;
        }

        // Permanente exige foto de face; visitante é cadastrado só com código + validade nativa.
        if (user.Type == UserType.Permanent && user.FacePhoto is null)
        {
            _logger.LogWarning("Usuário {UserId} não tem foto de face cadastrada; sync ignorado.", userId);
            return;
        }
        if (user.Type == UserType.Visitor && user.ValidUntil is null)
        {
            _logger.LogWarning("Visitante {UserId} sem validade definida; sync ignorado.", userId);
            return;
        }

        var desiredControllerIds = user.Permissions
            .Select(p => p.ControllerId)
            .Distinct()
            .ToHashSet();

        var existingStatuses = await _db.SyncStatuses
            .Where(s => s.UserId == userId)
            .ToListAsync(ct);

        var now = DateTime.UtcNow;
        foreach (var controllerId in desiredControllerIds)
        {
            var status = existingStatuses.FirstOrDefault(s => s.ControllerId == controllerId);
            if (status is { State: SyncState.Synced }) continue; // já ok, idempotente

            // Backoff/quarentena: Failed com NextRetryAtUtc futuro ainda aguarda a janela;
            // Failed com null é falha permanente (só ação manual reseta). Sem este filtro, um
            // usuário reenfileirado por outro caminho re-enviaria a face antes da hora.
            if (status is { State: SyncState.Failed } &&
                (status.NextRetryAtUtc is null || status.NextRetryAtUtc > now))
                continue;

            await SyncToControllerAsync(user, controllerId, status, ct);
        }

        // Permissão removida: revogar nos controladores onde a pessoa não deveria mais existir.
        var toRevoke = existingStatuses
            .Where(s => s.State == SyncState.Synced && !desiredControllerIds.Contains(s.ControllerId));
        foreach (var status in toRevoke)
        {
            await RevokeFromControllerAsync(user.UserCode, status, ct);
        }
    }

    public async Task RevokeUserAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return;

        var statuses = await _db.SyncStatuses
            .Where(s => s.UserId == userId && s.State != SyncState.Revoked)
            .ToListAsync(ct);

        foreach (var status in statuses)
        {
            await RevokeFromControllerAsync(user.UserCode, status, ct);
        }
    }

    private async Task SyncToControllerAsync(User user, Guid controllerId, DeviceSyncStatus? status, CancellationToken ct)
    {
        var controller = await _db.Controllers.FirstOrDefaultAsync(c => c.Id == controllerId, ct);
        if (controller is null) return;

        if (status is null)
        {
            status = new DeviceSyncStatus { UserId = user.Id, ControllerId = controllerId };
            _db.SyncStatuses.Add(status);
        }

        try
        {
            if (user.Type == UserType.Visitor)
            {
                // Visitante: pessoa sem face, com validade nativa (Person.Expiry) — o controlador
                // valida o vencimento offline; o QR carrega o código.
                await _gateway.AddPersonWithoutFaceAsync(controller, user, ct);
                status.State = SyncState.Synced;
                status.LastError = null;
                status.ConflictUserCode = null;
                status.RetryCount = 0;
                status.NextRetryAtUtc = null;
            }
            else
            {
                var result = await _gateway.AddPersonWithFaceAsync(controller, user, user.FacePhoto!, ct);
                status.State = result.Success ? SyncState.Synced : SyncState.Failed;
                status.LastError = result.Success ? null : result.Message;
                // Guarda o código do conflito só quando é duplicidade de face — a UI usa para
                // oferecer "substituir" ou "manter o existente".
                status.ConflictUserCode = result.Code == FaceUploadCode.Duplicate ? result.ConflictUserCode : null;
                if (result.Success)
                {
                    status.RetryCount = 0;
                    status.NextRetryAtUtc = null;
                }
                else
                {
                    status.RetryCount++;
                    // Falha permanente sai do retry automático (null); transitória agenda o backoff.
                    status.NextRetryAtUtc = SyncRetryPolicy.IsPermanent(result.Code)
                        ? null
                        : DateTime.UtcNow + SyncRetryPolicy.Backoff(status.RetryCount, _retryOptions);
                }
            }
        }
        catch (Exception ex)
        {
            // Exceção = falha transitória (aparelho offline, timeout): re-tenta com backoff.
            status.State = SyncState.Failed;
            status.RetryCount++;
            status.NextRetryAtUtc = DateTime.UtcNow + SyncRetryPolicy.Backoff(status.RetryCount, _retryOptions);
            status.LastError = ex.Message;
            _logger.LogError(ex, "Falha ao sincronizar usuário {UserId} no controlador {ControllerId}.", user.Id, controllerId);
        }

        status.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    public async Task ReplaceConflictAsync(Guid userId, Guid controllerId, CancellationToken ct = default)
    {
        var status = await _db.SyncStatuses.FirstOrDefaultAsync(s => s.UserId == userId && s.ControllerId == controllerId, ct);
        var controller = await _db.Controllers.FirstOrDefaultAsync(c => c.Id == controllerId, ct);
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (controller is null || user is null) return;

        // Exclui do controlador o usuário existente que colidiu (se conhecido), depois reenvia o novo.
        if (status?.ConflictUserCode is { } conflictCode && conflictCode != user.UserCode)
        {
            try { await _gateway.DeletePersonAsync(controller, conflictCode, ct); }
            catch (Exception ex) { _logger.LogWarning(ex, "Falha ao excluir usuário em conflito {Code} no controlador {ControllerId}.", conflictCode, controllerId); }
        }

        if (status is not null)
        {
            status.State = SyncState.Pending;
            status.ConflictUserCode = null;
            status.LastError = null;
            status.RetryCount = 0;
            status.NextRetryAtUtc = null;
            await _db.SaveChangesAsync(ct);
        }

        await SyncToControllerAsync(user, controllerId, status, ct);
    }

    public async Task KeepExistingOnConflictAsync(Guid userId, Guid controllerId, CancellationToken ct = default)
    {
        // "Manter o existente": cancela o envio deste usuário para esta porta — remove a permissão
        // e o status de sync correspondente, para não continuar tentando.
        var permission = await _db.Permissions.FirstOrDefaultAsync(p => p.UserId == userId && p.ControllerId == controllerId, ct);
        if (permission is not null) _db.Permissions.Remove(permission);

        var status = await _db.SyncStatuses.FirstOrDefaultAsync(s => s.UserId == userId && s.ControllerId == controllerId, ct);
        if (status is not null) _db.SyncStatuses.Remove(status);

        await _db.SaveChangesAsync(ct);
    }

    public async Task ForceResyncControllerAsync(Guid controllerId, CancellationToken ct = default)
    {
        var controller = await _db.Controllers.FirstOrDefaultAsync(c => c.Id == controllerId, ct);
        if (controller is null) return;

        // 1) Apaga TODAS as pessoas do dispositivo.
        await _gateway.ClearAllPersonsAsync(controller, ct);

        // 2) Zera os status de sync deste controlador (o dispositivo está vazio agora).
        var statuses = await _db.SyncStatuses.Where(s => s.ControllerId == controllerId).ToListAsync(ct);
        foreach (var s in statuses) _db.SyncStatuses.Remove(s);
        await _db.SaveChangesAsync(ct);

        // 3) Reenvia todos os usuários ativos com permissão neste controlador.
        var userIds = await _db.Permissions
            .Where(p => p.ControllerId == controllerId)
            .Select(p => p.UserId)
            .Distinct()
            .ToListAsync(ct);

        foreach (var userId in userIds)
        {
            var user = await _db.Users.Include(u => u.Permissions).FirstOrDefaultAsync(u => u.Id == userId, ct);
            if (user is null || user.RevokedAtUtc is not null) continue;
            if (user.Type == UserType.Permanent && user.FacePhoto is null) continue;
            if (user.Type == UserType.Visitor && (user.ValidUntil is null || user.ValidUntil < DateTime.UtcNow)) continue;

            await SyncToControllerAsync(user, controllerId, null, ct);
        }
    }

    private async Task RevokeFromControllerAsync(uint userCode, DeviceSyncStatus status, CancellationToken ct)
    {
        var controller = await _db.Controllers.FirstOrDefaultAsync(c => c.Id == status.ControllerId, ct);
        if (controller is null) return;

        try
        {
            await _gateway.DeletePersonAsync(controller, userCode, ct);
            status.State = SyncState.Revoked;
            status.LastError = null;
            status.RetryCount = 0;
            status.NextRetryAtUtc = null;
        }
        catch (Exception ex)
        {
            // Revogação falha é sempre transitória (aparelho inacessível): backoff e re-tenta.
            status.State = SyncState.Failed;
            status.RetryCount++;
            status.NextRetryAtUtc = DateTime.UtcNow + SyncRetryPolicy.Backoff(status.RetryCount, _retryOptions);
            status.LastError = ex.Message;
            _logger.LogError(ex, "Falha ao revogar usuário {UserCode} no controlador {ControllerId}.", userCode, status.ControllerId);
        }

        status.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }
}
