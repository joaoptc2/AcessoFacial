using HospitalAccess.Api.Services;
using Xunit;

namespace HospitalAccess.Tests.Access;

/// <summary>
/// Alocação das 64 grades de horário de uma controladora.
///
/// <para>
/// O que está em jogo: o comando AddTimeGroup substitui as 64 de uma vez, e grade sem definição
/// chega ao aparelho como SEMPRE FECHADO. Um erro aqui não dá tela de erro — tranca portas de
/// hospital. Por isso a grade 1 é reservada como "sem restrição" e nunca pode ser entregue a
/// outro conteúdo.
/// </para>
/// </summary>
public class TimeGroupAllocationTests
{
    private static ScheduleWindow J(int dia, string inicio, string fim) =>
        new((byte)dia, TimeOnly.Parse(inicio), TimeOnly.Parse(fim));

    /// <summary>Diurno de segunda a sexta — o caso comum de um hospital.</summary>
    private static IReadOnlyList<ScheduleWindow> Diurno() =>
        [.. Enumerable.Range(0, 5).Select(d => J(d, "07:00", "19:00"))];

    // ---------------------------------------------------------------- a grade reservada

    [Fact]
    public void SemJanelaNenhuma_VaiParaAGradeReservada()
    {
        // "Não defini horário" e "liberado o tempo todo" são a mesma coisa para o aparelho.
        var r = TimeGroupAllocation.Allocate(new Dictionary<int, string>(), []);

        Assert.True(r.Ok);
        Assert.Equal(TimeGroupAllocation.ReservedUnrestricted, r.GroupNumber);
        Assert.True(r.Reused);
    }

    [Fact]
    public void SeteDiasInteiros_SaoReconhecidosComoSemRestricao()
    {
        Assert.True(TimeGroupAllocation.IsUnrestricted(TimeGroupAllocation.UnrestrictedWindows));

        var r = TimeGroupAllocation.Allocate(new Dictionary<int, string>(), TimeGroupAllocation.UnrestrictedWindows);
        Assert.Equal(TimeGroupAllocation.ReservedUnrestricted, r.GroupNumber);
    }

    [Fact]
    public void SeisDiasInteiros_NaoSaoSemRestricao()
    {
        // Faltando o domingo, NÃO é livre — e tratar como livre daria acesso indevido.
        var seisDias = TimeGroupAllocation.UnrestrictedWindows.Where(w => w.Weekday != 6).ToList();
        Assert.False(TimeGroupAllocation.IsUnrestricted(seisDias));
    }

    /// <summary>
    /// A regra mais importante do arquivo: conteúdo restrito JAMAIS ocupa a grade 1, mesmo com o
    /// aparelho vazio. Se ocupasse, todas as permissões que apontam para a 1 — as que não têm
    /// horário definido — passariam a valer aquele horário restrito de repente.
    /// </summary>
    [Fact]
    public void ConteudoRestrito_NuncaOcupaAGradeReservada()
    {
        var r = TimeGroupAllocation.Allocate(new Dictionary<int, string>(), Diurno());

        Assert.True(r.Ok);
        Assert.NotEqual(TimeGroupAllocation.ReservedUnrestricted, r.GroupNumber);
        Assert.Equal(2, r.GroupNumber);
        Assert.False(r.Reused);
    }

    // ---------------------------------------------------------------- reaproveitamento

    [Fact]
    public void ConteudoIgual_ReaproveitaAGradeExistente()
    {
        var ocupadas = new Dictionary<int, string>
        {
            [1] = "livre",
            [2] = TimeGroupAllocation.Fingerprint(Diurno()),
        };

        var r = TimeGroupAllocation.Allocate(ocupadas, Diurno());

        Assert.Equal(2, r.GroupNumber);
        Assert.True(r.Reused);
    }

    /// <summary>
    /// A ordem em que o operador digitou não pode gerar uma grade nova — senão cem pessoas no
    /// mesmo turno gastariam as 64 posições por causa de digitação.
    /// </summary>
    [Fact]
    public void MesmasJanelasEmOrdemDiferente_TemAMesmaImpressao()
    {
        var a = Diurno();
        var b = Diurno().Reverse().ToList();

        Assert.Equal(TimeGroupAllocation.Fingerprint(a), TimeGroupAllocation.Fingerprint(b));

        var ocupadas = new Dictionary<int, string> { [1] = "livre", [7] = TimeGroupAllocation.Fingerprint(a) };
        var r = TimeGroupAllocation.Allocate(ocupadas, b);

        Assert.Equal(7, r.GroupNumber);
        Assert.True(r.Reused);
    }

    [Fact]
    public void JanelasDuplicadas_NaoMudamAImpressao()
    {
        var comDuplicata = Diurno().Concat([J(0, "07:00", "19:00")]).ToList();
        Assert.Equal(TimeGroupAllocation.Fingerprint(Diurno()), TimeGroupAllocation.Fingerprint(comDuplicata));
    }

    [Fact]
    public void ConteudoDiferente_GanhaGradeNova()
    {
        var noturno = Enumerable.Range(0, 5).Select(d => J(d, "19:00", "23:59")).ToList();
        var ocupadas = new Dictionary<int, string>
        {
            [1] = "livre",
            [2] = TimeGroupAllocation.Fingerprint(Diurno()),
        };

        var r = TimeGroupAllocation.Allocate(ocupadas, noturno);

        Assert.Equal(3, r.GroupNumber);
        Assert.False(r.Reused);
    }

    [Fact]
    public void GradeNova_PegaOMenorNumeroLivre()
    {
        // Buracos deixados por grades liberadas precisam ser reocupados, senão as 64 acabam
        // mesmo com espaço sobrando.
        var ocupadas = new Dictionary<int, string> { [1] = "livre", [2] = "aaa", [4] = "bbb", [5] = "ccc" };

        var r = TimeGroupAllocation.Allocate(ocupadas, Diurno());

        Assert.Equal(3, r.GroupNumber);
    }

    // ---------------------------------------------------------------- esgotamento

    [Fact]
    public void SemNumeroLivre_DevolveVazioEmVezDeSobrescrever()
    {
        // 1 reservada + 2..64 ocupadas com conteúdos distintos.
        var ocupadas = Enumerable.Range(1, TimeGroupAllocation.MaxGroups)
            .ToDictionary(n => n, n => n == 1 ? "livre" : $"hash-{n}");

        var r = TimeGroupAllocation.Allocate(ocupadas, Diurno());

        Assert.False(r.Ok);
        Assert.Null(r.GroupNumber);
    }

    [Fact]
    public void LotadaMasComConteudoIgual_AindaReaproveita()
    {
        var ocupadas = Enumerable.Range(1, TimeGroupAllocation.MaxGroups)
            .ToDictionary(n => n, n => n == 1 ? "livre" : $"hash-{n}");
        ocupadas[40] = TimeGroupAllocation.Fingerprint(Diurno());

        var r = TimeGroupAllocation.Allocate(ocupadas, Diurno());

        Assert.Equal(40, r.GroupNumber);
        Assert.True(r.Reused);
    }

    // ---------------------------------------------------------------- validação

    [Fact]
    public void JanelaValida_Passa() => Assert.Null(TimeGroupAllocation.Validate(Diurno()));

    [Fact]
    public void ListaVazia_Passa() => Assert.Null(TimeGroupAllocation.Validate([]));

    [Fact]
    public void FimAntesDoInicio_EhRecusado()
    {
        var erro = TimeGroupAllocation.Validate([J(0, "19:00", "07:00")]);
        Assert.NotNull(erro);
        Assert.Contains("termina antes", erro);
    }

    [Fact]
    public void FimIgualAoInicio_EhRecusado() =>
        Assert.NotNull(TimeGroupAllocation.Validate([J(0, "08:00", "08:00")]));

    [Fact]
    public void DiaForaDaSemana_EhRecusado()
    {
        var erro = TimeGroupAllocation.Validate([J(7, "08:00", "12:00")]);
        Assert.NotNull(erro);
        Assert.Contains("Dia da semana", erro);
    }

    [Fact]
    public void MaisDeOitoJanelasNoMesmoDia_EhRecusado()
    {
        // O hardware guarda 8 por dia; a nona seria descartada em silêncio pelo aparelho.
        var nove = Enumerable.Range(0, 9).Select(i => J(0, $"{i:00}:00", $"{i:00}:30")).ToList();

        var erro = TimeGroupAllocation.Validate(nove);
        Assert.NotNull(erro);
        Assert.Contains("8 janelas", erro);
    }

    [Fact]
    public void OitoJanelasNoMesmoDia_Passa()
    {
        var oito = Enumerable.Range(0, 8).Select(i => J(0, $"{i:00}:00", $"{i:00}:30")).ToList();
        Assert.Null(TimeGroupAllocation.Validate(oito));
    }

    [Fact]
    public void JanelasSobrepostasNoMesmoDia_SaoRecusadas()
    {
        var erro = TimeGroupAllocation.Validate([J(0, "08:00", "12:00"), J(0, "11:00", "14:00")]);
        Assert.NotNull(erro);
        Assert.Contains("sobrep", erro);
    }

    [Fact]
    public void JanelasEncostadas_NaoSaoSobreposicao() =>
        Assert.Null(TimeGroupAllocation.Validate([J(0, "08:00", "12:00"), J(0, "12:00", "18:00")]));

    [Fact]
    public void MesmoHorarioEmDiasDiferentes_NaoEhSobreposicao() =>
        Assert.Null(TimeGroupAllocation.Validate([J(0, "08:00", "12:00"), J(1, "08:00", "12:00")]));

    // ---------------------------------------------------------------- índices do protocolo

    [Fact]
    public void IndicesDeJanela_SaoAtribuidosPorDiaAPartirDeZero()
    {
        var janelas = new[] { J(0, "13:00", "18:00"), J(0, "07:00", "12:00"), J(3, "07:00", "12:00") };

        var comIndice = TimeGroupAllocation.WithSegmentIndexes(janelas);

        // Dia 0 recebe 0 e 1, em ordem de horário (não na ordem digitada); dia 3 recomeça em 0.
        Assert.Equal(3, comIndice.Count);
        Assert.Contains(comIndice, x => x.Weekday == 0 && x.SegmentIndex == 0 && x.Begin == new TimeOnly(7, 0));
        Assert.Contains(comIndice, x => x.Weekday == 0 && x.SegmentIndex == 1 && x.Begin == new TimeOnly(13, 0));
        Assert.Contains(comIndice, x => x.Weekday == 3 && x.SegmentIndex == 0);
    }

    [Fact]
    public void GradeReservada_CobreOsSeteDiasInteiros()
    {
        var comIndice = TimeGroupAllocation.WithSegmentIndexes(TimeGroupAllocation.UnrestrictedWindows);

        Assert.Equal(7, comIndice.Count);
        for (byte dia = 0; dia <= 6; dia++)
        {
            Assert.Contains(comIndice, x =>
                x.Weekday == dia && x.SegmentIndex == 0
                && x.Begin == new TimeOnly(0, 0) && x.End == TimeGroupAllocation.DayEnd);
        }
    }
}
