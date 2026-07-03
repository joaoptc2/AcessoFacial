using HospitalAccess.Domain.Enums;

namespace HospitalAccess.Domain.Entities;

/// <summary>
/// Estado de sincronização de um usuário em UM controlador.
/// Permite mostrar na UI se cada pessoa existe em cada porta e re-tentar falhas.
/// </summary>
public class DeviceSyncStatus
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }
    public User? User { get; set; }

    public Guid ControllerId { get; set; }
    public Controller? Controller { get; set; }

    public SyncState State { get; set; } = SyncState.Pending;
    public int RetryCount { get; set; }
    public string? LastError { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Quando a falha é "foto/feature duplicado", guarda o código do usuário já existente no
    /// controlador cuja face colidiu — para a UI oferecer "substituir" (excluir o existente e
    /// enviar o novo) ou "manter o existente".
    /// </summary>
    public uint? ConflictUserCode { get; set; }
}
