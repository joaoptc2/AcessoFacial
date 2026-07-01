using HospitalAccess.Domain.Enums;

namespace HospitalAccess.Domain.Entities;

/// <summary>
/// Usuário do sistema. Mapeia para o "Person" do dispositivo.
/// UserCode é o identificador numérico usado pelo controlador (uint no SDK).
/// </summary>
public class User
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Código numérico do usuário no controlador (SDK usa uint).</summary>
    public uint UserCode { get; set; }

    public string Name { get; set; } = string.Empty;
    public UserType Type { get; set; } = UserType.Permanent;

    /// <summary>Validade do acesso. Para visitante, é a base da validade do QR.</summary>
    public DateTime? ValidFrom { get; set; }
    public DateTime? ValidUntil { get; set; }

    /// <summary>Grupo de horário no dispositivo (1-64). Restringir para visitantes.</summary>
    public int TimeGroup { get; set; } = 1;

    /// <summary>Foto de face (JPG). Convertida p/ 480x640 / <=120KB antes do upload.</summary>
    public byte[]? FacePhoto { get; set; }

    /// <summary>Rastreabilidade: username (StaffUser) que cadastrou esta credencial.</summary>
    public string? CreatedByUsername { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Rastreabilidade: username (StaffUser) que revogou esta credencial, se aplicável.</summary>
    public string? RevokedByUsername { get; set; }
    public DateTime? RevokedAtUtc { get; set; }

    public ICollection<AccessPermission> Permissions { get; set; } = new List<AccessPermission>();
    public ICollection<DeviceSyncStatus> SyncStatuses { get; set; } = new List<DeviceSyncStatus>();
}
