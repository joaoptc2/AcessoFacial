using HospitalAccess.Application.Sync;
using HospitalAccess.Domain.Entities;
using HospitalAccess.Domain.Enums;
using HospitalAccess.Gateway;
using HospitalAccess.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace HospitalAccess.Api.Services;

/// <summary>
/// Orquestra o cadastro/remoção de usuários nos controladores, com fila de retry por
/// dispositivo (DeviceSyncStatus). Só se aplica a usuários PERMANENTES (acesso por
/// face) — visitantes (UserType.Visitor) usam QR com validade embutida e validação
/// offline no próprio controlador (Appendix 8), então não há nada para sincronizar
/// no hardware para eles; ver comentário em RunAsync/IVisitorExpirationJob.
/// </summary>
public sealed class UserSyncService : IUserSyncService
{
    private readonly AccessDbContext _db;
    private readonly IDeviceGateway _gateway;
    private readonly ILogger<UserSyncService> _logger;

    public UserSyncService(AccessDbContext db, IDeviceGateway gateway, ILogger<UserSyncService> logger)
    {
        _db = db;
        _gateway = gateway;
        _logger = logger;
    }

    public async Task SyncUserAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await _db.Users
            .Include(u => u.Permissions)
            .FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return;

        if (user.Type == UserType.Visitor)
        {
            // Visitante: QR de acesso (Appendix 8) é validado offline pelo controlador.
            // Não há Person para cadastrar/sincronizar no dispositivo.
            return;
        }

        if (user.FacePhoto is null)
        {
            _logger.LogWarning("Usuário {UserId} não tem foto de face cadastrada; sync ignorado.", userId);
            return;
        }

        var desiredControllerIds = user.Permissions
            .Select(p => p.ControllerId)
            .Distinct()
            .ToHashSet();

        var existingStatuses = await _db.SyncStatuses
            .Where(s => s.UserId == userId)
            .ToListAsync(ct);

        foreach (var controllerId in desiredControllerIds)
        {
            var status = existingStatuses.FirstOrDefault(s => s.ControllerId == controllerId);
            if (status is { State: SyncState.Synced }) continue; // já ok, idempotente

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

    public async Task RetryPendingAsync(CancellationToken ct = default)
    {
        var pending = await _db.SyncStatuses
            .Where(s => s.State == SyncState.Pending || s.State == SyncState.Failed)
            .Select(s => s.UserId)
            .Distinct()
            .ToListAsync(ct);

        foreach (var userId in pending)
        {
            try
            {
                await SyncUserAsync(userId, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha ao reprocessar sync pendente do usuário {UserId}.", userId);
            }
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
            var result = await _gateway.AddPersonWithFaceAsync(controller, user, user.FacePhoto!, ct);
            status.State = result.Success ? SyncState.Synced : SyncState.Failed;
            status.LastError = result.Success ? null : result.Message;
            if (!result.Success) status.RetryCount++;
        }
        catch (Exception ex)
        {
            status.State = SyncState.Failed;
            status.RetryCount++;
            status.LastError = ex.Message;
            _logger.LogError(ex, "Falha ao sincronizar usuário {UserId} no controlador {ControllerId}.", user.Id, controllerId);
        }

        status.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
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
        }
        catch (Exception ex)
        {
            status.State = SyncState.Failed;
            status.RetryCount++;
            status.LastError = ex.Message;
            _logger.LogError(ex, "Falha ao revogar usuário {UserCode} no controlador {ControllerId}.", userCode, status.ControllerId);
        }

        status.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }
}
