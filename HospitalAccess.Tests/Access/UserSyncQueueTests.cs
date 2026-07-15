using HospitalAccess.Api.Services;
using Xunit;

namespace HospitalAccess.Tests.Access;

/// <summary>
/// Testa a coordenação por usuário da fila de sincronização: exclusão por usuário (1 item por vez),
/// deduplicação e o "rerun" (uma re-sync pedida durante o processamento não se perde).
/// </summary>
public class UserSyncQueueTests
{
    private static readonly Guid A = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid B = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    private static bool TryDequeue(UserSyncQueue q, out SyncWork work) => q.Reader.TryRead(out work!);

    [Fact]
    public void EnqueueSync_SameUserTwice_ProducesSingleItem()
    {
        var q = new UserSyncQueue();
        q.EnqueueSync(A);
        q.EnqueueSync(A); // já ativo → não gera segundo item (marca rerun)

        Assert.True(TryDequeue(q, out var w));
        Assert.Equal(A, Assert.IsType<SyncUserWork>(w).UserId);
        Assert.False(TryDequeue(q, out _)); // não há segundo item
    }

    [Fact]
    public void CompleteSync_WithRerunRequested_Requeues()
    {
        var q = new UserSyncQueue();
        q.EnqueueSync(A);
        Assert.True(TryDequeue(q, out _)); // worker "pega" A
        q.EnqueueSync(A);                  // edição durante o processamento → pede rerun

        q.CompleteSync(A);                 // worker termina → deve reenfileirar

        Assert.True(TryDequeue(q, out var w));
        Assert.Equal(A, Assert.IsType<SyncUserWork>(w).UserId);
    }

    [Fact]
    public void CompleteSync_WithoutRerun_Releases()
    {
        var q = new UserSyncQueue();
        q.EnqueueSync(A);
        Assert.True(TryDequeue(q, out _));
        q.CompleteSync(A); // sem rerun → libera (não reenfileira)

        Assert.False(TryDequeue(q, out _));

        // Depois de liberado, um novo EnqueueSync volta a produzir item.
        q.EnqueueSync(A);
        Assert.True(TryDequeue(q, out var w));
        Assert.Equal(A, Assert.IsType<SyncUserWork>(w).UserId);
    }

    [Fact]
    public void EnqueueMany_DedupsActiveUsers()
    {
        var q = new UserSyncQueue();
        q.EnqueueSync(A);
        // A já ativo; B novo → só B conta.
        Assert.Equal(1, q.EnqueueMany(new[] { A, B }));
    }

    [Fact]
    public void EnqueueRevoke_IsNotDeduped()
    {
        var q = new UserSyncQueue();
        q.EnqueueRevoke(A);
        q.EnqueueRevoke(A);

        Assert.True(TryDequeue(q, out var w1));
        Assert.True(TryDequeue(q, out var w2));
        Assert.IsType<RevokeUserWork>(w1);
        Assert.IsType<RevokeUserWork>(w2);
    }
}
