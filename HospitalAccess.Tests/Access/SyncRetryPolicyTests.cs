using HospitalAccess.Api.Options;
using HospitalAccess.Api.Services;
using HospitalAccess.Domain.Entities;
using HospitalAccess.Domain.Enums;
using HospitalAccess.Gateway;
using Xunit;

namespace HospitalAccess.Tests.Access;

/// <summary>
/// Trava a política de retry que protege os controladores da tempestade de re-uploads:
/// classificação permanente×transitória, série do backoff exponencial e o filtro de
/// elegibilidade usado pela varredura automática.
/// </summary>
public class SyncRetryPolicyTests
{
    private static readonly SyncRetryOptions Defaults = new();

    [Theory]
    [InlineData(FaceUploadCode.NoFaceInPhoto)]
    [InlineData(FaceUploadCode.FeatureCodeUnidentifiable)]
    [InlineData(FaceUploadCode.Duplicate)]
    public void IsPermanent_FailuresThatNeverSucceedWithSameData_AreQuarantined(FaceUploadCode code) =>
        Assert.True(SyncRetryPolicy.IsPermanent(code));

    [Theory]
    [InlineData(FaceUploadCode.CrcFailure)]
    [InlineData(FaceUploadCode.UserNotFound)]
    [InlineData(FaceUploadCode.Ok)]
    public void IsPermanent_TransientOrNonFailureCodes_AreNot(FaceUploadCode code) =>
        Assert.False(SyncRetryPolicy.IsPermanent(code));

    [Fact]
    public void Backoff_DefaultSeries_DoublesFromTwoMinutesAndCapsAtOneHour()
    {
        // 1ª..7ª falha: 2, 4, 8, 16, 32, 60 (teto), 60...
        Assert.Equal(TimeSpan.FromMinutes(2), SyncRetryPolicy.Backoff(1, Defaults));
        Assert.Equal(TimeSpan.FromMinutes(4), SyncRetryPolicy.Backoff(2, Defaults));
        Assert.Equal(TimeSpan.FromMinutes(8), SyncRetryPolicy.Backoff(3, Defaults));
        Assert.Equal(TimeSpan.FromMinutes(16), SyncRetryPolicy.Backoff(4, Defaults));
        Assert.Equal(TimeSpan.FromMinutes(32), SyncRetryPolicy.Backoff(5, Defaults));
        Assert.Equal(TimeSpan.FromMinutes(60), SyncRetryPolicy.Backoff(6, Defaults));
        Assert.Equal(TimeSpan.FromMinutes(60), SyncRetryPolicy.Backoff(7, Defaults));
    }

    [Fact]
    public void Backoff_RetryCountZeroOrNegative_UsesBase()
    {
        // Robustez: contadores fora do esperado não podem gerar espera negativa/zero.
        Assert.Equal(TimeSpan.FromMinutes(2), SyncRetryPolicy.Backoff(0, Defaults));
        Assert.Equal(TimeSpan.FromMinutes(2), SyncRetryPolicy.Backoff(-1, Defaults));
    }

    /// <summary>Random com sorteio fixo — NextDouble devolve sempre o mesmo valor.</summary>
    private sealed class FixedRandom(double value) : Random
    {
        public override double NextDouble() => value;
    }

    private static readonly DateTime Now = new(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void DeferForUnhealthyPort_OpenCircuit_StartsAfterReopenPlusMinimumDelay()
    {
        var reabre = Now.AddMinutes(8);

        Assert.Equal(reabre.AddMinutes(1), SyncRetryPolicy.DeferForUnhealthyPort(Now, reabre, new FixedRandom(0)));
        // Sorteio máximo: a janela inteira de 5 min depois da folga.
        Assert.Equal(reabre.AddMinutes(6), SyncRetryPolicy.DeferForUnhealthyPort(Now, reabre, new FixedRandom(1)));
    }

    [Fact]
    public void DeferForUnhealthyPort_ClosedOrExpiredCircuit_CountsFromNow()
    {
        // Porta sem prova de vida, mas com o circuito fechado (ou já vencido): a referência é agora.
        Assert.Equal(Now.AddMinutes(1), SyncRetryPolicy.DeferForUnhealthyPort(Now, null, new FixedRandom(0)));
        Assert.Equal(Now.AddMinutes(1), SyncRetryPolicy.DeferForUnhealthyPort(Now, Now.AddMinutes(-3), new FixedRandom(0)));
    }

    [Fact]
    public void DeferForUnhealthyPort_UsersDeferredTogether_DoNotConverge()
    {
        // O defeito de nov/2026: todos os usuários de uma porta caíam no MESMO instante e chegavam
        // juntos. Com o sorteio, 50 adiamentos feitos no mesmo momento se espalham pela janela.
        var reabre = Now.AddMinutes(4);
        var random = new Random(20261118);
        var instantes = Enumerable.Range(0, 50)
            .Select(_ => SyncRetryPolicy.DeferForUnhealthyPort(Now, reabre, random))
            .ToList();

        Assert.All(instantes, t => Assert.InRange(t, reabre.AddMinutes(1), reabre.AddMinutes(6)));
        Assert.True(instantes.Distinct().Count() >= 45, "os adiamentos colidiram no mesmo instante");
        Assert.True(instantes.Max() - instantes.Min() >= TimeSpan.FromMinutes(3),
            $"espalhamento de só {(instantes.Max() - instantes.Min()).TotalSeconds:F0} s numa janela de 5 min");
    }

    [Fact]
    public void EligibleForAutoRetry_SelectsExactlyPendingAndDueFailures()
    {
        var now = new DateTime(2026, 7, 16, 12, 0, 0, DateTimeKind.Utc);
        var pending = new DeviceSyncStatus { State = SyncState.Pending };
        var failedDue = new DeviceSyncStatus { State = SyncState.Failed, NextRetryAtUtc = now.AddMinutes(-1) };
        var failedWaiting = new DeviceSyncStatus { State = SyncState.Failed, NextRetryAtUtc = now.AddMinutes(10) };
        var failedQuarantined = new DeviceSyncStatus { State = SyncState.Failed, NextRetryAtUtc = null };
        var synced = new DeviceSyncStatus { State = SyncState.Synced };
        var revoked = new DeviceSyncStatus { State = SyncState.Revoked };

        var eligible = new[] { pending, failedDue, failedWaiting, failedQuarantined, synced, revoked }
            .AsQueryable()
            .Where(SyncRetryPolicy.EligibleForAutoRetry(now))
            .ToList();

        Assert.Contains(pending, eligible);
        Assert.Contains(failedDue, eligible);
        Assert.DoesNotContain(failedWaiting, eligible);   // backoff ainda não venceu
        Assert.DoesNotContain(failedQuarantined, eligible); // permanente: só ação manual
        Assert.DoesNotContain(synced, eligible);
        Assert.DoesNotContain(revoked, eligible);
    }

    [Fact]
    public void EligibleForAutoRetry_FailureDueExactlyNow_IsEligible()
    {
        var now = new DateTime(2026, 7, 16, 12, 0, 0, DateTimeKind.Utc);
        var boundary = new DeviceSyncStatus { State = SyncState.Failed, NextRetryAtUtc = now };

        var eligible = new[] { boundary }.AsQueryable()
            .Where(SyncRetryPolicy.EligibleForAutoRetry(now)).ToList();

        Assert.Contains(boundary, eligible);
    }
}
