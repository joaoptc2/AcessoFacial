using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace HospitalAccess.Infrastructure.Persistence;

/// <summary>
/// Fábrica usada apenas pelas ferramentas de design-time (dotnet ef migrations/update). Não é
/// usada em runtime — lá o container injeta o DbContext com o DataProtection real. Aqui basta um
/// protetor no-op (nenhum dado é criptografado durante a geração de migração) e uma connection
/// string placeholder (o comando "migrations add" não conecta ao banco).
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AccessDbContext>
{
    public AccessDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AccessDbContext>()
            .UseNpgsql("Host=localhost;Database=hospital_access_design;Username=design;Password=design")
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
