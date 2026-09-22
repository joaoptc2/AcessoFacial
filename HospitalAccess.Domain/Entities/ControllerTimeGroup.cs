namespace HospitalAccess.Domain.Entities;

/// <summary>
/// Uma das 64 grades de horário DE UMA CONTROLADORA. Substitui a antiga grade global: o
/// hardware guarda 64 grades por aparelho, e cada aparelho tem a sua tabela — tratar isso como
/// uma definição única para os 30 desperdiçava espaço e impedia horários diferentes por porta.
///
/// <para>
/// As grades são ALOCADAS pelo sistema, não cadastradas por alguém: ao definir um horário para
/// um usuário numa porta, o alocador procura uma grade daquela porta que já tenha exatamente o
/// mesmo conteúdo e a reaproveita; só ocupa um número novo se não houver. Cem pessoas no mesmo
/// turno gastam uma grade, não cem.
/// </para>
/// <para>
/// A grade <see cref="ReservedUnrestrictedNumber"/> é reservada e significa SEM RESTRIÇÃO
/// (00:00–23:59, todos os dias). Ela existe por uma razão de segurança concreta: o comando
/// AddTimeGroup substitui as 64 de uma vez, e toda grade sem definição chega ao aparelho como
/// "sempre fechado". Sem uma grade livre garantida, a primeira sincronização trancaria todas as
/// portas do hospital.
/// </para>
/// </summary>
public class ControllerTimeGroup
{
    /// <summary>Número da grade reservada para "sem restrição". Nunca é alocada para outro conteúdo.</summary>
    public const int ReservedUnrestrictedNumber = 1;

    /// <summary>Capacidade do hardware: grades 1 a 64 por controladora.</summary>
    public const int MaxNumber = 64;

    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ControllerId { get; set; }
    public Controller? Controller { get; set; }

    /// <summary>Número da grade no aparelho (1-64).</summary>
    public int GroupNumber { get; set; }

    /// <summary>
    /// Impressão do conteúdo, usada para reaproveitar grades iguais. Duas grades com as mesmas
    /// janelas têm a mesma impressão, independente da ordem em que foram informadas.
    /// </summary>
    public string ContentHash { get; set; } = string.Empty;

    /// <summary>Rótulo livre, só para a tela ("Diurno", "Plantão noturno"). Não participa da comparação.</summary>
    public string Label { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public ICollection<ControllerTimeGroupSegment> Segments { get; set; } = new List<ControllerTimeGroupSegment>();
}
