using HospitalAccess.Domain.Enums;

namespace HospitalAccess.Api.Services;

/// <summary>Categoria (mutuamente exclusiva) de uma pendência de sincronização na tela de Sincronizações.</summary>
public enum SyncPendingCategory
{
    /// <summary>Aguardando a fila/varredura (elegível sempre).</summary>
    Pending,
    /// <summary>Falha cujo backoff já venceu — a próxima varredura reenfileira.</summary>
    DueNow,
    /// <summary>Falha aguardando o backoff exponencial vencer.</summary>
    WaitingBackoff,
    /// <summary>Falha permanente (foto sem rosto, feature ilegível) — só sai por ação manual.</summary>
    Quarantined,
    /// <summary>Duplicidade de face com outro cadastro — resolver por Substituir/Manter.</summary>
    Conflict,
}

/// <summary>O que a pendência fará quando processada.</summary>
public enum SyncPendingAction
{
    /// <summary>Enviar/atualizar o cadastro da pessoa na porta.</summary>
    Send,
    /// <summary>Remover a pessoa da porta (usuário revogado ou permissão retirada).</summary>
    Remove,
}

/// <summary>Derivações puras da tela de Sincronizações (testáveis sem EF).</summary>
public static class SyncOverviewRules
{
    /// <summary>
    /// Categoriza uma linha Pending/Failed de <see cref="Domain.Entities.DeviceSyncStatus"/>.
    /// Conflito tem precedência sobre quarentena (conflito também fica com NextRetryAtUtc null,
    /// mas tem fluxo próprio de resolução).
    /// </summary>
    public static SyncPendingCategory Categorize(SyncState state, DateTime? nextRetryAtUtc,
        uint? conflictUserCode, DateTime nowUtc)
    {
        if (state == SyncState.Failed && conflictUserCode != null) return SyncPendingCategory.Conflict;
        if (state == SyncState.Pending) return SyncPendingCategory.Pending;
        if (nextRetryAtUtc is null) return SyncPendingCategory.Quarantined;
        return nextRetryAtUtc <= nowUtc ? SyncPendingCategory.DueNow : SyncPendingCategory.WaitingBackoff;
    }

    /// <summary>
    /// Usuário revogado ou sem permissão na porta → a pendência é a REMOÇÃO da pessoa do
    /// aparelho (reconciliação/revogação); senão é o envio do cadastro.
    /// </summary>
    public static SyncPendingAction ResolveAction(bool userRevoked, bool hasPermission) =>
        userRevoked || !hasPermission ? SyncPendingAction.Remove : SyncPendingAction.Send;
}
