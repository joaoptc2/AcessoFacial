using System.Collections.Concurrent;
using System.Threading.Channels;

namespace HospitalAccess.Api.Services;

/// <summary>Item de trabalho de sincronização processado serialmente pela fila.</summary>
public abstract record SyncWork;
public sealed record SyncUserWork(Guid UserId) : SyncWork;
public sealed record RevokeUserWork(Guid UserId) : SyncWork;
public sealed record RevokeDeletedUserWork(uint UserCode, IReadOnlyList<Guid> ControllerIds) : SyncWork;

/// <summary>
/// Fila de sincronização com o hardware. Cada cadastro/edição/revogação era disparado num
/// <c>Task.Run</c> imediato, então um lote de cadastros gerava vários <c>AddPersonAndImage</c>
/// (upload de face) CONCORRENTES no mesmo controlador — que estourava com CommandStatus_Timeout.
/// Aqui os pedidos são enfileirados e processados UM DE CADA VEZ por <see cref="UserSyncQueueWorker"/>,
/// enviando "por partes" e sem sobrecarregar o aparelho. Idempotente: reenfileirar o mesmo usuário
/// é seguro (o sync relê o estado atual do banco).
/// </summary>
public interface IUserSyncQueue
{
    /// <summary>Enfileira (com deduplicação) a sincronização de um usuário.</summary>
    void EnqueueSync(Guid userId);

    /// <summary>Enfileira a revogação de um usuário ainda cadastrado no banco.</summary>
    void EnqueueRevoke(Guid userId);

    /// <summary>Enfileira a revogação de um usuário já excluído (por código + controladores capturados antes do delete).</summary>
    void EnqueueRevokeDeleted(uint userCode, IReadOnlyList<Guid> controllerIds);

    /// <summary>Enfileira (com deduplicação) a sincronização de vários usuários. Devolve quantos foram efetivamente enfileirados.</summary>
    int EnqueueMany(IEnumerable<Guid> userIds);
}

public sealed class UserSyncQueue : IUserSyncQueue
{
    private readonly Channel<SyncWork> _channel = Channel.CreateUnbounded<SyncWork>(
        new UnboundedChannelOptions { SingleReader = true });

    // Deduplicação só das sincronizações pendentes: evita a fila crescer com o mesmo usuário
    // (ex.: o job de retry a cada 2 min reenfileirando os mesmos que falharam). Revogações não são
    // deduplicadas (são pontuais). O worker remove o id ao retirar da fila (MarkDequeued), então uma
    // edição durante o processamento reenfileira e é reprocessada.
    private readonly ConcurrentDictionary<Guid, byte> _queuedSync = new();

    internal ChannelReader<SyncWork> Reader => _channel.Reader;

    public void EnqueueSync(Guid userId)
    {
        if (!_queuedSync.TryAdd(userId, 0)) return; // já na fila
        _channel.Writer.TryWrite(new SyncUserWork(userId));
    }

    public void EnqueueRevoke(Guid userId) => _channel.Writer.TryWrite(new RevokeUserWork(userId));

    public void EnqueueRevokeDeleted(uint userCode, IReadOnlyList<Guid> controllerIds)
    {
        if (controllerIds.Count == 0) return;
        _channel.Writer.TryWrite(new RevokeDeletedUserWork(userCode, controllerIds));
    }

    public int EnqueueMany(IEnumerable<Guid> userIds)
    {
        var count = 0;
        foreach (var id in userIds)
        {
            if (!_queuedSync.TryAdd(id, 0)) continue;
            _channel.Writer.TryWrite(new SyncUserWork(id));
            count++;
        }
        return count;
    }

    /// <summary>Libera o id da deduplicação (chamado pelo worker ao retirar o item da fila).</summary>
    internal void MarkDequeued(Guid userId) => _queuedSync.TryRemove(userId, out _);
}
