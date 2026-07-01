using HospitalAccess.Domain.Enums;

namespace HospitalAccess.Domain.Entities;

/// <summary>
/// Registro de alarme de hardware (incêndio, coação, sabotagem, arrombamento, lista negra,
/// timeout de abertura etc.). Append-only, mesmo padrão do AccessLog — não há FK/navegação
/// para Controller de propósito, para nunca perder histórico se o controlador for excluído.
/// </summary>
public class AlarmEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;

    public Guid ControllerId { get; set; }
    public string ControllerName { get; set; } = string.Empty;

    public AlarmKind Kind { get; set; }

    /// <summary>Código cru do protocolo (AlarmTransaction.TransactionCode, incluindo variantes de cancelamento).</summary>
    public int RawEventCode { get; set; }

    /// <summary>Verdadeiro quando o código bruto representa o cancelamento/encerramento do alarme, não o disparo.</summary>
    public bool Cleared { get; set; }
}
