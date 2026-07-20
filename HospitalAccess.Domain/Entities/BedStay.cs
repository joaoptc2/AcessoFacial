using HospitalAccess.Domain.Enums;

namespace HospitalAccess.Domain.Entities;

/// <summary>
/// Internação: um paciente ocupando um LEITO (1 leito = 1 controlador/porta, modelo do
/// sistema). Linha ativa = <see cref="EndedAtUtc"/> null (índice único filtrado garante no
/// máximo 1 internação ativa por leito). As linhas encerradas são o histórico de mudanças de
/// leito — transferência encerra a internação de origem e abre outra no destino.
/// O nome do paciente é cadastrado manualmente (integração com o sistema hospitalar é futura);
/// o acesso físico do paciente é um <see cref="User"/> visitante criado automaticamente
/// (QR/sincronização/revogação reutilizam o fluxo validado em hardware).
/// </summary>
public class BedStay
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>O leito (controlador/porta do quarto).</summary>
    public Guid ControllerId { get; set; }
    public Controller? Controller { get; set; }

    /// <summary>Nome do paciente (snapshot manual — não é FK).</summary>
    public string PatientName { get; set; } = string.Empty;

    /// <summary>
    /// User visitante criado para o acesso (QR). Null se a internação foi registrada sem
    /// acesso físico; SetNull se o User for excluído (o histórico permanece pelo snapshot).
    /// </summary>
    public Guid? VisitorUserId { get; set; }
    public User? VisitorUser { get; set; }

    public DateTime StartedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Null = internação ATIVA.</summary>
    public DateTime? EndedAtUtc { get; set; }

    public BedStayEndReason? EndReason { get; set; }

    public string? CreatedByUsername { get; set; }
    public string? EndedByUsername { get; set; }
}
