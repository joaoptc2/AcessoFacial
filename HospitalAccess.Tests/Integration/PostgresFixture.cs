using HospitalAccess.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace HospitalAccess.Tests.Integration;

/// <summary>
/// Banco PostgreSQL descartável para os testes de integração.
///
/// <para>
/// POR QUE POSTGRES DE VERDADE, e não o provider InMemory: o caminho que estes testes cobrem usa
/// <c>ExecuteUpdateAsync</c>/<c>ExecuteDeleteAsync</c>, <c>nextval</c> de sequence, índices únicos
/// PARCIAIS (<c>HasFilter</c>) e <c>xmin</c> como token de concorrência otimista. O InMemory não
/// implementa nenhum dos quatro — um teste que passasse nele não provaria nada sobre produção.
/// </para>
/// <para>
/// A conexão vem de <c>HOSPITALACCESS_TEST_DB</c>. Sem a variável, os testes são PULADOS com
/// motivo visível (nunca aprovados em silêncio). Para rodar localmente:
/// <code>
/// docker run --rm -d -p 5432:5432 -e POSTGRES_PASSWORD=postgres --name ha-test postgres:16
/// export HOSPITALACCESS_TEST_DB="Host=127.0.0.1;Port=5432;Username=postgres;Password=postgres;Database=postgres"
/// </code>
/// No CI, um service container do PostgreSQL fornece a mesma variável (ver .github/workflows/ci.yml).
/// </para>
/// <para>
/// Cada fixture cria um BANCO próprio (nome aleatório) e o derruba no fim, então classes de teste
/// rodando em paralelo não disputam as mesmas tabelas.
/// </para>
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    public const string ConnectionEnvVar = "HOSPITALACCESS_TEST_DB";

    private string? _adminConnectionString;
    private string _databaseName = string.Empty;

    /// <summary>Connection string do banco descartável, ou null quando não há servidor configurado.</summary>
    public string? ConnectionString { get; private set; }

    /// <summary>Motivo do skip quando <see cref="ConnectionString"/> é null.</summary>
    public string SkipReason =>
        $"Defina {ConnectionEnvVar} com a conexão de um PostgreSQL de teste para rodar os testes de integração.";

    public bool Available => ConnectionString is not null;

    public async Task InitializeAsync()
    {
        _adminConnectionString = Environment.GetEnvironmentVariable(ConnectionEnvVar);
        if (string.IsNullOrWhiteSpace(_adminConnectionString)) return;

        _databaseName = $"ha_test_{Guid.NewGuid():N}";

        await using (var admin = new NpgsqlConnection(_adminConnectionString))
        {
            await admin.OpenAsync();
            await using var cmd = admin.CreateCommand();
            // O nome é gerado aqui (hex de um Guid), não vem de entrada externa — mas o identificador
            // ainda vai entre aspas para não depender de case folding do servidor.
            cmd.CommandText = $"CREATE DATABASE \"{_databaseName}\"";
            await cmd.ExecuteNonQueryAsync();
        }

        ConnectionString = new NpgsqlConnectionStringBuilder(_adminConnectionString)
        {
            Database = _databaseName,
        }.ConnectionString;

        // EnsureCreated (não Migrate): monta o schema a partir do MODELO ATUAL, que é o que os
        // testes exercitam. Migrations têm cobertura própria — aqui interessa o comportamento do
        // serviço contra o schema que o código espera.
        await using var db = CreateContext();
        await db.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        if (_adminConnectionString is null || _databaseName.Length == 0) return;

        // Sem limpar o pool, as conexões ociosas do EF impedem o DROP DATABASE.
        NpgsqlConnection.ClearAllPools();

        await using var admin = new NpgsqlConnection(_adminConnectionString);
        await admin.OpenAsync();
        await using var cmd = admin.CreateCommand();
        cmd.CommandText = $"DROP DATABASE IF EXISTS \"{_databaseName}\" WITH (FORCE)";
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Novo contexto apontando para o banco descartável. Cada teste usa o seu (o DbContext não é
    /// thread-safe e o serviço testado guarda o dele).
    /// </summary>
    public AccessDbContext CreateContext()
    {
        if (ConnectionString is null)
            throw new InvalidOperationException(SkipReason);

        var options = new DbContextOptionsBuilder<AccessDbContext>()
            .UseNpgsql(ConnectionString)
            .EnableSensitiveDataLogging()
            .Options;

        // Protetor efêmero: as senhas de controlador são cifradas em repouso pelo ValueConverter do
        // AccessDbContext, então o contexto exige um IDataProtectionProvider mesmo nos testes.
        return new AccessDbContext(options, DataProtectionProvider.Create("HospitalAccess.Tests"));
    }
}

/// <summary>
/// Compartilha um único banco entre as classes de teste da coleção — criar e derrubar um banco
/// por classe custa mais do que o isolamento oferece, já que cada teste usa dados próprios.
/// </summary>
[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
