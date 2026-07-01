namespace HospitalAccess.Domain.Enums;

/// <summary>Estado da sincronização de um usuário em um controlador específico.</summary>
public enum SyncState
{
    Pending = 0,   // ainda não enviado
    Synced = 1,    // confirmado no dispositivo
    Failed = 2,    // falhou; requer retry
    Revoked = 3    // removido do dispositivo
}
