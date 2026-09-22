using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using HospitalAccess.Domain.Entities;

namespace HospitalAccess.Api.Services;

/// <summary>Uma janela de horário pedida pelo operador, antes de virar grade no aparelho.</summary>
/// <param name="Weekday">0 = segunda-feira ... 6 = domingo.</param>
public readonly record struct ScheduleWindow(byte Weekday, TimeOnly Begin, TimeOnly End);

/// <summary>Resultado de uma alocação.</summary>
/// <param name="GroupNumber">Número da grade no aparelho, ou null se não havia espaço.</param>
/// <param name="Reused">True = uma grade de conteúdo idêntico já existia e foi reaproveitada.</param>
public readonly record struct AllocationResult(int? GroupNumber, bool Reused)
{
    public bool Ok => GroupNumber is not null;
}

/// <summary>
/// Regras puras da alocação de grades de horário nas 64 posições de uma controladora. Sem I/O —
/// a persistência e o envio ao aparelho ficam no <see cref="ControllerTimeGroupService"/>.
///
/// <para>
/// POR QUE ISTO É CRÍTICO: o comando AddTimeGroup do protocolo substitui as 64 grades de uma vez,
/// e toda grade sem definição chega ao aparelho como "sempre fechado" — não como "livre". Assim
/// que o sistema passa a escrever essa tabela, ele assume a responsabilidade por ela inteira. A
/// grade 1 é reservada e SEMPRE gravada como 00:00–23:59 nos sete dias, e é para ela que aponta
/// toda permissão sem horário definido. Sem essa reserva, a primeira sincronização trancaria
/// todas as portas do hospital.
/// </para>
/// </summary>
public static class TimeGroupAllocation
{
    public const int ReservedUnrestricted = ControllerTimeGroup.ReservedUnrestrictedNumber;
    public const int MaxGroups = ControllerTimeGroup.MaxNumber;
    public const int MaxSegmentsPerDay = ControllerTimeGroupSegment.MaxSegmentsPerDay;

    private static readonly TimeOnly DayStart = new(0, 0);

    /// <summary>
    /// Fim do dia como o aparelho entende. O protocolo grava hora e minuto (sem segundos), então
    /// 23:59 é o último minuto acessível — não existe 24:00.
    /// </summary>
    public static readonly TimeOnly DayEnd = new(23, 59);

    /// <summary>As janelas da grade reservada: o dia inteiro, todos os dias.</summary>
    public static IReadOnlyList<ScheduleWindow> UnrestrictedWindows { get; } =
        [.. Enumerable.Range(0, 7).Select(d => new ScheduleWindow((byte)d, DayStart, DayEnd))];

    // ------------------------------------------------------------------ validação

    /// <summary>Null = válido; senão a mensagem para o operador.</summary>
    public static string? Validate(IReadOnlyList<ScheduleWindow> windows)
    {
        if (windows.Count == 0) return null; // vazio = sem restrição, tratado como a grade 1

        foreach (var w in windows)
        {
            if (w.Weekday > 6)
                return $"Dia da semana inválido ({w.Weekday}) — use 0 (segunda) a 6 (domingo).";
            if (w.End <= w.Begin)
                return $"A janela {w.Begin:HH\\:mm}–{w.End:HH\\:mm} termina antes de começar. "
                     + "Para atravessar a meia-noite, use duas janelas em dias diferentes.";
        }

        foreach (var dia in windows.GroupBy(w => w.Weekday))
        {
            if (dia.Count() > MaxSegmentsPerDay)
                return $"O aparelho aceita no máximo {MaxSegmentsPerDay} janelas por dia; "
                     + $"o dia {dia.Key} tem {dia.Count()}.";

            // Sobreposição no mesmo dia: o aparelho aceita, mas o resultado é ambíguo para quem
            // lê a configuração depois — e costuma ser erro de digitação, não intenção.
            var ordenadas = dia.OrderBy(w => w.Begin).ToList();
            for (var i = 1; i < ordenadas.Count; i++)
            {
                if (ordenadas[i].Begin < ordenadas[i - 1].End)
                    return $"As janelas {ordenadas[i - 1].Begin:HH\\:mm}–{ordenadas[i - 1].End:HH\\:mm} e "
                         + $"{ordenadas[i].Begin:HH\\:mm}–{ordenadas[i].End:HH\\:mm} se sobrepõem no mesmo dia.";
            }
        }

        return null;
    }

    // ------------------------------------------------------------------ forma canônica

    /// <summary>
    /// Forma canônica: ordenada por dia e hora, sem duplicatas exatas. É o que torna a comparação
    /// independente da ordem em que o operador digitou as janelas — duas grades "iguais mas
    /// digitadas fora de ordem" precisam reaproveitar o mesmo número.
    /// </summary>
    public static IReadOnlyList<ScheduleWindow> Canonicalize(IEnumerable<ScheduleWindow> windows) =>
        [.. windows.Distinct().OrderBy(w => w.Weekday).ThenBy(w => w.Begin).ThenBy(w => w.End)];

    /// <summary>
    /// Sem restrição? Todos os sete dias cobertos das 00:00 às 23:59. Uma lista vazia também
    /// conta: "não defini horário" e "liberado o tempo todo" são a mesma coisa para o aparelho.
    /// </summary>
    public static bool IsUnrestricted(IReadOnlyList<ScheduleWindow> windows)
    {
        if (windows.Count == 0) return true;

        for (byte dia = 0; dia <= 6; dia++)
        {
            if (!windows.Any(w => w.Weekday == dia && w.Begin == DayStart && w.End >= DayEnd))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Impressão do conteúdo, para reaproveitar grades iguais. Duas listas com as mesmas janelas
    /// dão a mesma impressão, em qualquer ordem. Não é segredo: é só uma chave de comparação
    /// curta e estável para guardar no banco.
    /// </summary>
    public static string Fingerprint(IEnumerable<ScheduleWindow> windows)
    {
        var canonica = Canonicalize(windows);
        if (canonica.Count == 0) return "livre";

        var texto = new StringBuilder();
        foreach (var w in canonica)
        {
            texto.Append(w.Weekday.ToString(CultureInfo.InvariantCulture)).Append(':')
                 .Append(w.Begin.ToString("HHmm", CultureInfo.InvariantCulture)).Append('-')
                 .Append(w.End.ToString("HHmm", CultureInfo.InvariantCulture)).Append(';');
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(texto.ToString()));
        return Convert.ToHexString(bytes)[..16];
    }

    // ------------------------------------------------------------------ alocação

    /// <summary>
    /// Escolhe o número de grade para um conteúdo numa controladora.
    ///
    /// <para>
    /// Na ordem: sem restrição devolve a grade reservada; conteúdo já presente reaproveita a
    /// grade existente; senão ocupa o MENOR número livre a partir de 2. Sem número livre, devolve
    /// vazio — e o chamador precisa dizer isso ao operador em vez de gravar qualquer coisa.
    /// </para>
    /// </summary>
    /// <param name="ocupadas">Número da grade → impressão do conteúdo, para esta controladora.</param>
    public static AllocationResult Allocate(
        IReadOnlyDictionary<int, string> ocupadas, IReadOnlyList<ScheduleWindow> desejadas)
    {
        if (IsUnrestricted(desejadas)) return new AllocationResult(ReservedUnrestricted, Reused: true);

        var impressao = Fingerprint(desejadas);

        foreach (var (numero, hash) in ocupadas.OrderBy(p => p.Key))
        {
            if (numero != ReservedUnrestricted && hash == impressao)
                return new AllocationResult(numero, Reused: true);
        }

        for (var numero = ReservedUnrestricted + 1; numero <= MaxGroups; numero++)
        {
            if (!ocupadas.ContainsKey(numero)) return new AllocationResult(numero, Reused: false);
        }

        return new AllocationResult(null, Reused: false);
    }

    /// <summary>
    /// Atribui os índices de janela (0-7) exigidos pelo aparelho, por dia. O operador informa as
    /// janelas; o índice é detalhe do protocolo e não deveria aparecer na tela.
    /// </summary>
    public static IReadOnlyList<(byte Weekday, byte SegmentIndex, TimeOnly Begin, TimeOnly End)>
        WithSegmentIndexes(IEnumerable<ScheduleWindow> windows)
    {
        var resultado = new List<(byte, byte, TimeOnly, TimeOnly)>();
        foreach (var dia in Canonicalize(windows).GroupBy(w => w.Weekday))
        {
            byte indice = 0;
            foreach (var w in dia)
            {
                if (indice >= MaxSegmentsPerDay) break;
                resultado.Add((w.Weekday, indice, w.Begin, w.End));
                indice++;
            }
        }
        return resultado;
    }
}
