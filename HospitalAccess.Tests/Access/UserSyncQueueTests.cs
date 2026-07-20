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

    [Fact]
    public void EnqueueChangeRoom_CarriesSnapshotAndIsNotDeduped()
    {
        var q = new UserSyncQueue();
        var oldRooms = new[] { B };
        // Como as revogações, cada troca é um item próprio (carrega o snapshot das portas antigas).
        q.EnqueueChangeRoom(A, 42, oldRooms);
        q.EnqueueChangeRoom(A, 42, oldRooms);

        Assert.True(TryDequeue(q, out var w1));
        var work = Assert.IsType<ChangeVisitorRoomWork>(w1);
        Assert.Equal(A, work.UserId);
        Assert.Equal(42u, work.UserCode);
        Assert.Equal(oldRooms, work.OldControllerIds);
        Assert.True(TryDequeue(q, out var w2));
        Assert.IsType<ChangeVisitorRoomWork>(w2);
    }

    [Fact]
    public void EnqueueChangeRoom_DoesNotBlockSyncDedupForSameUser()
    {
        var q = new UserSyncQueue();
        q.EnqueueChangeRoom(A, 42, new[] { B });
        // A troca de quarto não marca o usuário como "ativo" na dedup de sync:
        // um EnqueueSync posterior ainda produz o item dele normalmente.
        q.EnqueueSync(A);

        Assert.True(TryDequeue(q, out var w1));
        Assert.IsType<ChangeVisitorRoomWork>(w1);
        Assert.True(TryDequeue(q, out var w2));
        Assert.IsType<SyncUserWork>(w2);
    }

    [Fact]
    public void ActiveUserIds_TracksQueuedUsers_ProcessingStartsEmpty()
    {
        var q = new UserSyncQueue();
        q.EnqueueSync(A);

        Assert.Contains(A, q.ActiveUserIds);
        Assert.Empty(q.ProcessingUsers); // nenhum worker pegou o item ainda
    }

    [Fact]
    public void BeginProcessing_ExposesUserWithTimestamp_CompleteSyncClearsBoth()
    {
        var q = new UserSyncQueue();
        q.EnqueueSync(A);
        Assert.True(TryDequeue(q, out _)); // worker "pega" A
        var before = DateTime.UtcNow;
        q.BeginProcessing(A);

        Assert.True(q.ProcessingUsers.TryGetValue(A, out var since));
        Assert.InRange(since, before, DateTime.UtcNow);

        q.CompleteSync(A); // sem rerun → sai de ambos
        Assert.Empty(q.ProcessingUsers);
        Assert.DoesNotContain(A, q.ActiveUserIds);
    }

    [Fact]
    public void CompleteSync_WithRerun_LeavesActiveButNotProcessing()
    {
        var q = new UserSyncQueue();
        q.EnqueueSync(A);
        Assert.True(TryDequeue(q, out _));
        q.BeginProcessing(A);
        q.EnqueueSync(A); // rerun pedido durante o processamento

        q.CompleteSync(A);

        // O rerun voltou para a FILA: continua ativo, mas ninguém o processa neste instante.
        Assert.DoesNotContain(A, q.ProcessingUsers.Keys);
        Assert.Contains(A, q.ActiveUserIds);
        Assert.True(TryDequeue(q, out var w));
        Assert.Equal(A, Assert.IsType<SyncUserWork>(w).UserId);
    }
}
