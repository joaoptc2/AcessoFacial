using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace HospitalAccess.Infrastructure.Persistence;

/// <summary>
/// Fábrica usada apenas pelas ferramentas de design-time (dotnet ef migrations/update). Não é
/// usada em runtime — lá o container injeta o DbContext com o DataProtection real. Aqui basta um
/// protetor no-op (nenhum dado é criptografado durante a geração de migração).
///
/// <para>
/// A connection string vem de <c>ConnectionStrings__Postgres</c> quando definida. O
/// <c>migrations add</c> não conecta ao banco e funciona com o placeholder; já o
/// <c>database update</c> CONECTA — com o placeholder fixo ele falhava sempre com
/// "password authentication failed for user 'design'". Produção continua aplicando o script
/// idempotente (docs/instalacao-servidor-linux.md §13); isto é conveniência de desenvolvimento.
/// </para>
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AccessDbContext>
{
    private const string PlaceholderConnectionString =
        "Host=localhost;Database=hospital_access_design;Username=design;Password=design";

    public AccessDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = PlaceholderConnectionString;

        var options = new DbContextOptionsBuilder<AccessDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new AccessDbContext(options, new NoOpDataProtectionProvider());
    }

    private sealed class NoOpDataProtectionProvider : IDataProtectionProvider, IDataProtector
    {
        public IDataProtector CreateProtector(string purpose) => this;
        public byte[] Protect(byte[] plaintext) => plaintext;
        public byte[] Unprotect(byte[] protectedData) => protectedData;
    }
}
