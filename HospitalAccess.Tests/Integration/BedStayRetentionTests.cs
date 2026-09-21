using HospitalAccess.Domain.Entities;
using HospitalAccess.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace HospitalAccess.Tests.Integration;

/// <summary>
/// Expurgo do histórico de internações. A regra que este arquivo existe para travar é uma só:
/// a internação ATIVA nunca sai, qualquer que seja o prazo. Ela não é histórico — é quem está
/// no leito agora. Apagá-la esvaziaria a Gestão de Leitos de um hospital cheio.
///
/// <para>
/// Precisa de PostgreSQL de verdade: o expurgo usa <c>ExecuteDeleteAsync</c>, que o provedor
/// traduz para DELETE ... WHERE no servidor e não tem equivalente em memória.
/// </para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class BedStayRetentionTests
{
    private readonly PostgresFixture _pg;

    public BedStayRetentionTests(PostgresFixture pg) => _pg = pg;

    /// <summary>Mesmo predicado do DataRetentionBackgroundService, contra o banco real.</summary>
    private static Task<int> ExpurgarAsync(Infrastructure.Persistence.AccessDbContext db, int dias)
    {
        if (dias <= 0) return Task.FromResult(0);
        var cutoff = DateTime.UtcNow.AddDays(-dias);
        return db.BedStays.Where(b => b.EndedAtUtc != null && b.EndedAtUtc < cutoff).ExecuteDeleteAsync();
    }

    private async Task<Guid> SemearLeitoAsync()
    {
        var id = Guid.NewGuid();
        await using var seed = _pg.CreateContext();
        seed.Controllers.Add(new Controller
        {
            Id = id,
            Name = $"Quarto {Random.Shared.Next(1000, 9999)}",
            IpAddress = "192.168.19.20",
            Port = 8000,
            SerialNumber = Random.Shared.NextInt64(1_000_000_000_000_000, 9_999_999_999_999_999).ToString(),
            IsRoom = true,
        });
        await seed.SaveChangesAsync();
        return id;
    }

    [SkippableFact]
    public async Task InternacaoAtiva_NuncaEhExpurgada_MesmoMuitoAntiga()
    {
        Skip.IfNot(_pg.Available, _pg.SkipReason);
        var leito = await SemearLeitoAsync();

        var ativaId = Guid.NewGuid();
        await using (var seed = _pg.CreateContext())
        {
            // Internação de 5 anos atrás e AINDA ABERTA — um paciente de longa permanência.
            seed.BedStays.Add(new BedStay
            {
                Id = ativaId,
                ControllerId = leito,
                PatientName = "Paciente internado há anos",
                StartedAtUtc = DateTime.UtcNow.AddYears(-5),
                EndedAtUtc = null,
            });
            await seed.SaveChangesAsync();
        }

        await using var db = _pg.CreateContext();
        var ativasAntes = await db.BedStays.CountAsync(b => b.EndedAtUtc == null);

        await ExpurgarAsync(db, dias: 1);

        Assert.True(await db.BedStays.AnyAsync(b => b.Id == ativaId));
        Assert.Equal(ativasAntes, await db.BedStays.CountAsync(b => b.EndedAtUtc == null));
    }

    [SkippableFact]
    public async Task InternacaoEncerradaAntiga_SaiEAsRecentesFicam()
    {
        Skip.IfNot(_pg.Available, _pg.SkipReason);
        var leito = await SemearLeitoAsync();

        var antiga = Guid.NewGuid();
        var recente = Guid.NewGuid();
        await using (var seed = _pg.CreateContext())
        {
            seed.BedStays.AddRange(
                new BedStay
                {
                    Id = antiga,
                    ControllerId = leito,
                    PatientName = "Alta antiga",
                    StartedAtUtc = DateTime.UtcNow.AddDays(-400),
                    EndedAtUtc = DateTime.UtcNow.AddDays(-370),
                    EndReason = BedStayEndReason.Discharge,
                },
                new BedStay
                {
                    Id = recente,
                    ControllerId = leito,
                    PatientName = "Alta recente",
                    StartedAtUtc = DateTime.UtcNow.AddDays(-10),
                    EndedAtUtc = DateTime.UtcNow.AddDays(-3),
                    EndReason = BedStayEndReason.Discharge,
                });
            await seed.SaveChangesAsync();
        }

        await using var db = _pg.CreateContext();
        await ExpurgarAsync(db, dias: 365);

        Assert.False(await db.BedStays.AnyAsync(b => b.Id == antiga));
        Assert.True(await db.BedStays.AnyAsync(b => b.Id == recente));
    }

    /// <summary>Prazo 0 = reter para sempre, igual às demais políticas de retenção.</summary>
    [SkippableFact]
    public async Task PrazoZero_NaoExpurgaNada()
    {
        Skip.IfNot(_pg.Available, _pg.SkipReason);
        var leito = await SemearLeitoAsync();

        var id = Guid.NewGuid();
        await using (var seed = _pg.CreateContext())
        {
            seed.BedStays.Add(new BedStay
            {
                Id = id,
                ControllerId = leito,
                PatientName = "Alta de 2020",
                StartedAtUtc = DateTime.UtcNow.AddYears(-6),
                EndedAtUtc = DateTime.UtcNow.AddYears(-6).AddDays(5),
                EndReason = BedStayEndReason.Discharge,
            });
            await seed.SaveChangesAsync();
        }

        await using var db = _pg.CreateContext();
        var totalAntes = await db.BedStays.CountAsync();

        Assert.Equal(0, await ExpurgarAsync(db, dias: 0));
        Assert.Equal(totalAntes, await db.BedStays.CountAsync());
        Assert.True(await db.BedStays.AnyAsync(b => b.Id == id));
    }
}
