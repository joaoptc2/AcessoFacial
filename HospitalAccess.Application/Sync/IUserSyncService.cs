using HospitalAccess.Domain.Entities;

namespace HospitalAccess.Application.Sync;

/// <summary>
/// Orquestra o cadastro/atualização/remoção de um usuário nos controladores em que ele
/// deve existir, mantendo DeviceSyncStatus por dispositivo, idempotente e re-tentável.
/// </summary>
public interface IUserSyncService
{
    /// <summary>Envia (ou reenvia) o usuário a todos os controladores das portas que ele pode acessar.</summary>
    Task SyncUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Remove o usuário dos controladores (ex.: visitante expirado).</summary>
    Task RevokeUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Reprocessa pendências/falhas (chamado por job periódico).</summary>
    Task RetryPendingAsync(CancellationToken ct = default);
}
