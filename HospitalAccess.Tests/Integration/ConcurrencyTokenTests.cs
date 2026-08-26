using HospitalAccess.Domain.Entities;
using HospitalAccess.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace HospitalAccess.Tests.Integration;

/// <summary>
/// Concorrência otimista pela coluna de sistema <c>xmin</c> do PostgreSQL.
///
/// <para>
/// Estes testes existem por causa da migração para o .NET 10: o atalho
/// <c>UseXminAsConcurrencyToken()</c> do provider Npgsql ficou obsoleto na linha 8 e foi
/// REMOVIDO na 10. O mapeamento foi reescrito à mão no <c>AccessDbContext</c>, e "compila" não
/// prova que continua funcionando — só um UPDATE concorrente de verdade prova. Sem isso, duas
/// pessoas editando o mesmo usuário voltariam ao last-write-wins SILENCIOSO, que é justamente o
/// que o token evita: uma permissão de porta desaparecendo sem ninguém saber por quê.
/// </para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ConcurrencyTokenTests
{
    private readonly PostgresFixture _pg;

    public ConcurrencyTokenTests(PostgresFixture pg) => _pg = pg;

    [SkippableFact]
    public async Task Usuario_EdicaoConcorrente_DisparaConflito()
    {
        Skip.IfNot(_pg.Available, _pg.SkipReason);

        var userId = Guid.NewGuid();
        await using (var seed = _pg.CreateContext())
        {
            seed.Users.Add(new User
            {
                Id = userId,
                UserCode = (uint)Random.Shared.Next(1, int.MaxValue),
                Name = "Original",
                Type = UserType.Permanent,
            });
            await seed.SaveChangesAsync();
        }

        // Dois contextos leem a MESMA versão da linha — duas telas abertas no mesmo cadastro.
        await using var contextA = _pg.CreateContext();
        await using var contextB = _pg.CreateContext();

        var userA = await contextA.Users.SingleAsync(u => u.Id == userId);
        var userB = await contextB.Users.SingleAsync(u => u.Id == userId);

        userA.Name = "Editado por A";
        await contextA.SaveChangesAsync();

        // B ainda tem o xmin antigo: o UPDATE não casa nenhuma linha e o EF acusa.
        userB.Name = "Editado por B";
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => contextB.SaveChangesAsync());

        // A escrita vencedora permanece — a perdedora NÃO sobrescreveu em silêncio.
        await using var check = _pg.CreateContext();
        var final = await check.Users.AsNoTracking().SingleAsync(u => u.Id == userId);
        Assert.Equal("Editado por A", final.Name);
    }

    [SkippableFact]
    public async Task Controlador_EdicaoConcorrente_DisparaConflito()
    {
        Skip.IfNot(_pg.Available, _pg.SkipReason);

        var controllerId = Guid.NewGuid();
        await using (var seed = _pg.CreateContext())
        {
            seed.Controllers.Add(new Controller
            {
                Id = controllerId,
                Name = "Porta Original",
                IpAddress = "192.168.19.20",
                Port = 8000,
                SerialNumber = Guid.NewGuid().ToString("N")[..16],
            });
            await seed.SaveChangesAsync();
        }

        await using var contextA = _pg.CreateContext();
        await using var contextB = _pg.CreateContext();

        var a = await contextA.Controllers.SingleAsync(c => c.Id == controllerId);
        var b = await contextB.Controllers.SingleAsync(c => c.Id == controllerId);

        a.Name = "Renomeada por A";
        await contextA.SaveChangesAsync();

        b.Name = "Renomeada por B";
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => contextB.SaveChangesAsync());
    }

    /// <summary>
    /// O token NÃO pode atrapalhar o caminho normal: reler depois do conflito e salvar de novo
    /// tem de funcionar (é o que o operador faz ao ver "recarregue e tente novamente").
    /// </summary>
    [SkippableFact]
    public async Task AposConflito_RelerESalvarFunciona()
    {
        Skip.IfNot(_pg.Available, _pg.SkipReason);

        var userId = Guid.NewGuid();
        await using (var seed = _pg.CreateContext())
        {
            seed.Users.Add(new User
            {
                Id = userId,
                UserCode = (uint)Random.Shared.Next(1, int.MaxValue),
                Name = "Original",
                Type = UserType.Permanent,
            });
            await seed.SaveChangesAsync();
        }

        await using (var first = _pg.CreateContext())
        {
            var u = await first.Users.SingleAsync(x => x.Id == userId);
            u.Name = "Primeira edição";
            await first.SaveChangesAsync();
        }

        await using (var second = _pg.CreateContext())
        {
            var u = await second.Users.SingleAsync(x => x.Id == userId);
            u.Name = "Segunda edição";
            await second.SaveChangesAsync(); // sem conflito: leu a versão atual
        }

        await using var check = _pg.CreateContext();
        var final = await check.Users.AsNoTracking().SingleAsync(u => u.Id == userId);
        Assert.Equal("Segunda edição", final.Name);
    }

    /// <summary>
    /// O <c>xmin</c> é coluna de sistema: o mapeamento não pode ter criado uma coluna real na
    /// tabela. Se criasse, a migração teria mudado o schema em produção sem migration.
    /// </summary>
    [SkippableFact]
    public async Task Xmin_NaoViraColunaReal()
    {
        Skip.IfNot(_pg.Available, _pg.SkipReason);

        await using var db = _pg.CreateContext();
        var count = await db.Database
            .SqlQueryRaw<int>(
                """
                SELECT COUNT(*)::int AS "Value"
                FROM information_schema.columns
                WHERE table_name IN ('Users', 'Controllers') AND column_name = 'xmin'
                """)
            .SingleAsync();

        Assert.Equal(0, count);
    }
}
