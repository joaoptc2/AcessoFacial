namespace HospitalAccess.Domain.Entities;

/// <summary>
/// Define o que um "TimeGroup" (1-64, referenciado por User.TimeGroup/AccessPermission.TimeGroup)
/// significa em termos de horário (Classe VI — Opening Time Zone). Definição global, empurrada
/// (AddTimeGroup) para os 30 controladores — o banco é a fonte da verdade, não há leitura de volta.
/// </summary>
public class TimeGroupSchedule
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Número do grupo no dispositivo (1-64).</summary>
    public int GroupNumber { get; set; }

    public string Name { get; set; } = string.Empty;

    public ICollection<TimeGroupSegment> Segments { get; set; } = new List<TimeGroupSegment>();
}
