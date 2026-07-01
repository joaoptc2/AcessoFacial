namespace HospitalAccess.Domain.Entities;

/// <summary>
/// Foto capturada pelo controlador no momento de um evento de acesso (Classe XI do protocolo
/// — "recorded photos"), obtida por leitura sob demanda (não vem no push em tempo real).
/// Sem FK/navegação para Controller, mesmo motivo do AccessLog/AlarmEvent.
/// </summary>
public class EventPhoto
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ControllerId { get; set; }
    public string ControllerName { get; set; } = string.Empty;

    public uint? UserCode { get; set; }
    public DateTime CapturedAtUtc { get; set; }

    /// <summary>Código bruto do evento associado (authentication record), quando disponível.</summary>
    public int RawEventCode { get; set; }

    public byte[] ImageJpg { get; set; } = [];

    public DateTime DownloadedAtUtc { get; set; } = DateTime.UtcNow;
}
