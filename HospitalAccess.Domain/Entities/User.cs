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

    // --- Dados de cadastro (perfil) — todos opcionais. ---
    /// <summary>Documento de identificação (CPF ou RG).</summary>
    public string? Document { get; set; }
    /// <summary>Matrícula/registro do funcionário.</summary>
    public string? EmployeeId { get; set; }
    /// <summary>Cargo/função (ex.: Enfermeiro, Técnico, Médico).</summary>
    public string? JobTitle { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    /// <summary>Anotações livres sobre o usuário.</summary>
    public string? Notes { get; set; }

    /// <summary>Grupo organizacional (ex.: "Enfermagem"), só para organização — não é o TimeGroup do dispositivo.</summary>
    public Guid? GroupId { get; set; }
    public UserGroup? Group { get; set; }

    /// <summary>Validade do acesso. Para visitante, é a base da validade do QR.</summary>
    public DateTime? ValidFrom { get; set; }
    public DateTime? ValidUntil { get; set; }

    /// <summary>Grupo de horário no dispositivo (1-64). Restringir para visitantes.</summary>
    public int TimeGroup { get; set; } = 1;

    /// <summary>Foto de face (JPG). Convertida p/ 480x640 / <=120KB antes do upload.</summary>
    public byte[]? FacePhoto { get; set; }

    /// <summary>
    /// Número do cartão Mifare/IC, se o usuário também tiver um cartão físico (além da face).
    /// Mapeia para Person.CardData no SDK. A estrutura completa de setor Mifare (Apêndices
    /// 10-13 do protocolo) não é abstraída pelo SDK e exigiria um leitor/gravador de cartão
    /// dedicado — fora do alcance deste sistema, que só grava o número no controlador.
    /// </summary>
    public uint? CardNumber { get; set; }

    /// <summary>Rastreabilidade: username (StaffUser) que cadastrou esta credencial.</summary>
    public string? CreatedByUsername { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Rastreabilidade: username (StaffUser) que revogou esta credencial, se aplicável.</summary>
    public string? RevokedByUsername { get; set; }
    public DateTime? RevokedAtUtc { get; set; }

    public ICollection<AccessPermission> Permissions { get; set; } = new List<AccessPermission>();
    public ICollection<DeviceSyncStatus> SyncStatuses { get; set; } = new List<DeviceSyncStatus>();
}
