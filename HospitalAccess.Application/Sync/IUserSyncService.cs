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

    /// <summary>
    /// Resolve um conflito de face duplicada "substituindo": exclui do controlador o usuário
    /// existente que colidiu e reenvia o novo usuário.
    /// </summary>
    Task ReplaceConflictAsync(Guid userId, Guid controllerId, CancellationToken ct = default);

    /// <summary>
    /// Resolve um conflito de face duplicada "mantendo o existente": cancela o envio deste
    /// usuário para o controlador (remove a permissão dele naquela porta).
    /// </summary>
    Task KeepExistingOnConflictAsync(Guid userId, Guid controllerId, CancellationToken ct = default);

    /// <summary>
    /// Resincronização forçada de um controlador: apaga TODAS as pessoas do dispositivo e reenvia
    /// os usuários cadastrados no sistema com permissão nele.
    /// </summary>
    Task ForceResyncControllerAsync(Guid controllerId, CancellationToken ct = default);
}
