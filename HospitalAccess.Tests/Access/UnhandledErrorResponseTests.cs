using HospitalAccess.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace HospitalAccess.Tests.Access;

/// <summary>
/// Nasceu de um incidente: servidor atualizado sem a migration devolvia, na tela de
/// Configurações, só "Código de referência: …" — obrigando a ir ao log do servidor buscar uma
/// causa que o próprio sistema já tinha detectado no boot. O que estes testes protegem é isso:
/// a falha cuja causa é conhecida tem que virar instrução, e as demais NÃO podem ser
/// confundidas com ela.
/// </summary>
public class UnhandledErrorResponseTests
{
    private const string Correlation = "0HNOOT8P5QA25:00000001";
    private static readonly string[] Nenhuma = [];

    /// <summary>A PostgresException real exige o construtor completo (severidade, estado, mensagem).</summary>
    private static PostgresException Pg(string sqlState) =>
        new("erro simulado", "ERROR", "ERROR", sqlState);

    [Theory]
    [InlineData("42703")] // UndefinedColumn — coluna que a migration criaria
    [InlineData("42P01")] // UndefinedTable  — tabela que a migration criaria
    public void Coluna_ou_tabela_ausente_e_banco_desatualizado(string sqlState)
    {
        Assert.Equal(UnhandledErrorKind.StaleSchema, UnhandledErrorResponse.Classify(Pg(sqlState)));
    }

    /// <summary>
    /// Numa LEITURA a PostgresException chega crua; numa ESCRITA vem embrulhada pelo EF. Olhar
    /// só o topo da exceção deixaria metade dos casos cair no "erro inesperado".
    /// </summary>
    [Fact]
    public void Excecao_do_postgres_embrulhada_pelo_ef_tambem_e_reconhecida()
    {
        var embrulhada = new DbUpdateException("falha ao salvar", Pg("42703"));

        Assert.Equal(UnhandledErrorKind.StaleSchema, UnhandledErrorResponse.Classify(embrulhada));
    }

    [Fact]
    public void Banco_desatualizado_nomeia_a_migration_que_falta()
    {
        var mensagem = UnhandledErrorResponse.MessageFor(
            UnhandledErrorKind.StaleSchema, ["20260922193550_AddBedExtraAction"], Correlation);

        Assert.Contains("DESATUALIZADO", mensagem);
        Assert.Contains("20260922193550_AddBedExtraAction", mensagem);
        // O caminho do procedimento é a parte acionável — sem ele a mensagem só constata.
        Assert.Contains("instalacao-servidor-linux.md", mensagem);
    }

    /// <summary>
    /// O boot pode não ter conseguido listar as migrations (banco fora do ar naquele instante).
    /// A mensagem tem que continuar apontando o procedimento, sem prometer um nome que não tem.
    /// </summary>
    [Fact]
    public void Sem_lista_de_migrations_ainda_manda_aplicar_a_atualizacao()
    {
        var mensagem = UnhandledErrorResponse.MessageFor(UnhandledErrorKind.StaleSchema, Nenhuma, Correlation);

        Assert.Contains("DESATUALIZADO", mensagem);
        Assert.Contains("instalacao-servidor-linux.md", mensagem);
        Assert.DoesNotContain("falta aplicar:", mensagem);
    }

    [Fact]
    public void Valor_unico_repetido_continua_409_com_a_mensagem_de_sempre()
    {
        var erro = new DbUpdateException("falha ao salvar", Pg("23505"));

        var kind = UnhandledErrorResponse.Classify(erro);
        Assert.Equal(UnhandledErrorKind.UniqueViolation, kind);
        Assert.Equal(StatusCodes.Status409Conflict, UnhandledErrorResponse.StatusCodeFor(kind));
        Assert.Contains("valor único", UnhandledErrorResponse.MessageFor(kind, Nenhuma, Correlation));
    }

    [Fact]
    public void Conflito_de_concorrencia_continua_409()
    {
        var kind = UnhandledErrorResponse.Classify(new DbUpdateConcurrencyException("conflito"));

        Assert.Equal(UnhandledErrorKind.Concurrency, kind);
        Assert.Equal(StatusCodes.Status409Conflict, UnhandledErrorResponse.StatusCodeFor(kind));
        Assert.Contains("outra pessoa", UnhandledErrorResponse.MessageFor(kind, Nenhuma, Correlation));
    }

    /// <summary>
    /// O que NÃO é diagnosticável continua 500 com o código de correlação — a mensagem
    /// acionável do banco desatualizado não pode virar o texto padrão de qualquer falha.
    /// </summary>
    [Fact]
    public void Erro_sem_diagnostico_continua_500_com_codigo_de_referencia()
    {
        var kind = UnhandledErrorResponse.Classify(new InvalidOperationException("qualquer coisa"));

        Assert.Equal(UnhandledErrorKind.Unexpected, kind);
        Assert.Equal(StatusCodes.Status500InternalServerError, UnhandledErrorResponse.StatusCodeFor(kind));
        var mensagem = UnhandledErrorResponse.MessageFor(kind, ["20260922193550_AddBedExtraAction"], Correlation);
        Assert.Contains(Correlation, mensagem);
        Assert.DoesNotContain("DESATUALIZADO", mensagem);
    }

    [Fact]
    public void Sem_excecao_nao_inventa_diagnostico()
    {
        Assert.Equal(UnhandledErrorKind.Unexpected, UnhandledErrorResponse.Classify(null));
    }
}
