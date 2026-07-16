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
