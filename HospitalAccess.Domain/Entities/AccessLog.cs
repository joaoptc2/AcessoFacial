using HospitalAccess.Domain.Enums;

namespace HospitalAccess.Domain.Entities;

/// <summary>
/// Registro de acesso. Append-only / imutável — requisito de auditoria hospitalar.
/// Alimentado pelos eventos em tempo real que os controladores empurram.
/// </summary>
public class AccessLog
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;

    public uint? UserCode { get; set; }
    public string? UserName { get; set; }

    public Guid ControllerId { get; set; }
    public string ControllerName { get; set; } = string.Empty;
    public string? DoorName { get; set; }

    public AccessMethod Method { get; set; }

    /// <summary>Código de evento cru do protocolo (ver "authentication record").</summary>
    public int RawEventCode { get; set; }

    /// <summary>1 = entrada, 2 = saída (checktype do protocolo).</summary>
    public int? Direction { get; set; }

    public bool Granted { get; set; }
}
