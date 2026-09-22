namespace HospitalAccess.Domain.Entities;

/// <summary>
/// Uma janela de horário (início-fim) de um dia da semana dentro de uma
/// <see cref="ControllerTimeGroup"/>. O aparelho aceita até 8 janelas por dia por grade; um dia
/// sem nenhuma janela fica FECHADO naquele dia, e não livre — é o contrário do que a intuição
/// sugere e a origem do risco de trancar tudo.
/// </summary>
public class ControllerTimeGroupSegment
{
    /// <summary>Janelas por dia suportadas pelo hardware.</summary>
    public const int MaxSegmentsPerDay = 8;

    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ControllerTimeGroupId { get; set; }
    public ControllerTimeGroup? Group { get; set; }

    /// <summary>0 = segunda-feira ... 6 = domingo.</summary>
    public byte Weekday { get; set; }

    /// <summary>0-7 (até 8 janelas por dia, capacidade do hardware).</summary>
    public byte SegmentIndex { get; set; }

    public TimeOnly BeginTime { get; set; }
    public TimeOnly EndTime { get; set; }
}
