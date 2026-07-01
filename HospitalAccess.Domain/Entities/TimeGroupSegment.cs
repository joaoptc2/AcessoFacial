namespace HospitalAccess.Domain.Entities;

/// <summary>
/// Uma janela de horário (início-fim) de um dia da semana dentro de um TimeGroupSchedule.
/// O dispositivo suporta até 8 janelas por dia por grupo; dias/grupos sem nenhuma janela
/// ficam fechados (sem acesso) naquele dia.
/// </summary>
public class TimeGroupSegment
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TimeGroupScheduleId { get; set; }
    public TimeGroupSchedule? Schedule { get; set; }

    /// <summary>0 = segunda-feira ... 6 = domingo.</summary>
    public byte Weekday { get; set; }

    /// <summary>0-7 (até 8 janelas por dia, capacidade do hardware).</summary>
    public byte SegmentIndex { get; set; }

    public TimeOnly BeginTime { get; set; }
    public TimeOnly EndTime { get; set; }
}
