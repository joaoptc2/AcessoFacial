using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace HospitalAccess.Api.Services;

/// <summary>O que o operador precisa saber sobre uma exceção que chegou ao fim do pipeline.</summary>
public enum UnhandledErrorKind
{
    /// <summary>Sem diagnóstico possível — só o código de correlação para cruzar com o log.</summary>
    Unexpected,

    /// <summary>Índice único violado (SN/código repetido).</summary>
    UniqueViolation,

    /// <summary>Outra pessoa alterou o registro no meio da edição.</summary>
    Concurrency,

    /// <summary>
    /// Coluna ou tabela que o binário espera e o banco não tem: deploy sem a migration.
    /// É a única falha de 500 cuja CAUSA o sistema já conhece de antemão (o boot detecta as
    /// migrations pendentes), então é a única que pode virar instrução em vez de protocolo.
    /// </summary>
    StaleSchema,
}

/// <summary>
/// Traduz a exceção não tratada na resposta que o operador vê. Puro e testável de propósito:
/// esta classificação nasceu de um incidente (tela de Configurações devolvendo só um código de
/// referência num servidor atualizado sem a migration) e não pode regredir em silêncio.
/// </summary>
public static class UnhandledErrorResponse
{
    public static UnhandledErrorKind Classify(Exception? error)
    {
        if (error is null) return UnhandledErrorKind.Unexpected;
        if (error is DbUpdateConcurrencyException) return UnhandledErrorKind.Concurrency;
        if (HasPostgresState(error, PostgresErrorCodes.UniqueViolation)) return UnhandledErrorKind.UniqueViolation;
        if (HasPostgresState(error, PostgresErrorCodes.UndefinedColumn)
            || HasPostgresState(error, PostgresErrorCodes.UndefinedTable))
            return UnhandledErrorKind.StaleSchema;
        return UnhandledErrorKind.Unexpected;
    }

    public static int StatusCodeFor(UnhandledErrorKind kind) => kind switch
    {
        UnhandledErrorKind.UniqueViolation => StatusCodes.Status409Conflict,
        UnhandledErrorKind.Concurrency => StatusCodes.Status409Conflict,
        _ => StatusCodes.Status500InternalServerError,
    };

    public static string MessageFor(UnhandledErrorKind kind, IReadOnlyList<string> pendingMigrations, string correlationId) => kind switch
    {
        UnhandledErrorKind.UniqueViolation =>
            "Já existe um registro com esse valor único (ex.: número de série ou código já cadastrado).",
        UnhandledErrorKind.Concurrency =>
            "O registro foi alterado por outra pessoa enquanto você editava. Recarregue e tente novamente.",
        UnhandledErrorKind.StaleSchema =>
            "O banco de dados está DESATUALIZADO para esta versão do sistema"
            + (pendingMigrations.Count > 0
                ? $" — falta aplicar: {string.Join(", ", pendingMigrations)}"
                : " (falta uma coluna ou tabela)")
            + ". Aplique o script de atualização do banco (docs/instalacao-servidor-linux.md, seção 13) "
            + $"e reinicie o serviço. Código de referência: {correlationId}.",
        _ => $"Ocorreu um erro inesperado ao processar a solicitação. Código de referência: {correlationId}.",
    };

    /// <summary>
    /// A PostgresException pode vir CRUA (numa leitura) ou embrulhada em DbUpdateException
    /// (numa escrita) — olhar só o topo deixaria metade dos casos passar batido.
    /// </summary>
    private static bool HasPostgresState(Exception? error, string sqlState)
    {
        for (var current = error; current is not null; current = current.InnerException)
            if (current is PostgresException pg && pg.SqlState == sqlState)
                return true;
        return false;
    }
}
