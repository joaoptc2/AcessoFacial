namespace HospitalAccess.Domain.Enums;

/// <summary>Motivo do encerramento de uma internação (mudança de leito).</summary>
public enum BedStayEndReason
{
    /// <summary>Transferido para outro leito (uma nova internação ativa é criada no destino).</summary>
    Transfer = 1,

    /// <summary>Alta: o paciente saiu do hospital; o acesso é revogado.</summary>
    Discharge = 2,
}
