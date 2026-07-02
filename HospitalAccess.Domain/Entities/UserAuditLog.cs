namespace HospitalAccess.Domain.Entities;

/// <summary>
/// Histórico administrativo de um usuário/visitante (quem criou, editou, revogou, reativou
/// ou excluiu, e quando). Não tem FK para User — igual a AccessLog/EventPhoto, guarda um
/// snapshot (UserCode/UserName) para sobreviver a uma exclusão definitiva do cadastro.
/// Não confundir com AccessLog, que é o log de acesso físico (entrada/saída pela porta).
/// </summary>
public class UserAuditLog
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;

    public Guid UserId { get; set; }
    public uint UserCode { get; set; }
    public string UserName { get; set; } = string.Empty;

    /// <summary>"Criado", "Atualizado", "Revogado", "Reativado" ou "Excluído".</summary>
    public string Action { get; set; } = string.Empty;

    public string? PerformedByUsername { get; set; }

    /// <summary>Descrição livre do que mudou (ex.: "Portas: +Porta A, -Porta B").</summary>
    public string? Details { get; set; }
}
