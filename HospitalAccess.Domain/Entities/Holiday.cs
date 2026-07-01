namespace HospitalAccess.Domain.Entities;

/// <summary>
/// Feriado (Classe V do protocolo). Definição global de calendário mantida pela aplicação
/// e empurrada (AddHoliday) para os controladores — não existe leitura de volta, o banco é
/// a fonte da verdade. O dispositivo suporta no máximo 30 feriados (Index 1-30).
/// </summary>
public class Holiday
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Slot no dispositivo (1-30, capacidade do hardware).</summary>
    public byte Index { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>Data do feriado. Quando RepeatsYearly, apenas mês/dia importam para o dispositivo.</summary>
    public DateTime Date { get; set; }

    public bool RepeatsYearly { get; set; } = true;

    /// <summary>Categoria de feriado no dispositivo (1-3), usada na tabela de correspondência da grade horária.</summary>
    public byte HolidayType { get; set; } = 1;
}
