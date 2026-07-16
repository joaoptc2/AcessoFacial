using System.Collections.Concurrent;
using System.Threading.Channels;

namespace HospitalAccess.Api.Services;

/// <summary>Item de trabalho de sincronização processado serialmente pela fila.</summary>
public abstract record SyncWork;
public sealed record SyncUserWork(Guid UserId) : SyncWork;
public sealed record RevokeUserWork(Guid UserId) : SyncWork;
public sealed record RevokeDeletedUserWork(uint UserCode, IReadOnlyList<Guid> ControllerIds) : SyncWork;

/// <summary>
/// Troca de quarto de um visitante: limpa o QR do(s) quarto(s) antigo(s) via HTTP (best-effort)
/// e então REENFILEIRA a sincronização pela dedup por usuário (a reconciliação revoga via SDK
/// nos antigos e cadastra no novo). A limpeza do QR acontece antes do enqueue, então a ordem
/// das duas fases é preservada.
/// </summary>
public sealed record ChangeVisitorRoomWork(Guid UserId, uint UserCode, IReadOnlyList<Guid> OldControllerIds) : SyncWork;

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

    /// <summary>Enfileira a troca de quarto de um visitante (limpeza do QR antigo + re-sincronização).</summary>
    void EnqueueChangeRoom(Guid userId, uint userCode, IReadOnlyList<Guid> oldControllerIds);

    /// <summary>Enfileira (com deduplicação) a sincronização de vários usuários. Devolve quantos foram efetivamente enfileirados.</summary>
    int EnqueueMany(IEnumerable<Guid> userIds);
}

public sealed class UserSyncQueue : IUserSyncQueue
{
    // Vários workers consomem em paralelo (a serialização real é POR CONTROLADOR, via lock do gateway),
    // logo NÃO é SingleReader.
    private readonly Channel<SyncWork> _channel = Channel.CreateUnbounded<SyncWork>(
        new UnboundedChannelOptions { SingleReader = false });

    // Coordena as sincronizações por usuário entre os workers paralelos. Chave presente = o usuário
    // está NA FILA ou SENDO PROCESSADO (então só existe 1 item por usuário → nunca dois workers no
    // mesmo usuário). Valor = "rerun pedido": alguém pediu nova sync ENQUANTO ele era processado —
    // ao terminar, o worker reenfileira. Assim uma edição durante o processamento não se perde e o
    // job de retry (a cada 2 min) não empilha duplicatas. Revogações são pontuais (sem dedup).
    private readonly ConcurrentDictionary<Guid, bool> _active = new();

    internal ChannelReader<SyncWork> Reader => _channel.Reader;

    public void EnqueueSync(Guid userId) => EnqueueSyncCore(userId);

    private bool EnqueueSyncCore(Guid userId)
    {
        var added = false;
        // Ausente → entra como "sem rerun" e vira um item na fila. Já presente → só marca rerun.
        _active.AddOrUpdate(userId, _ => { added = true; return false; }, (_, _) => true);
        if (added) _channel.Writer.TryWrite(new SyncUserWork(userId));
        return added;
    }

    public void EnqueueRevoke(Guid userId) => _channel.Writer.TryWrite(new RevokeUserWork(userId));

    public void EnqueueRevokeDeleted(uint userCode, IReadOnlyList<Guid> controllerIds)
    {
        if (controllerIds.Count == 0) return;
        _channel.Writer.TryWrite(new RevokeDeletedUserWork(userCode, controllerIds));
    }

    // Sem dedup (como as revogações): cada troca de quarto carrega o snapshot das portas antigas.
    public void EnqueueChangeRoom(Guid userId, uint userCode, IReadOnlyList<Guid> oldControllerIds) =>
        _channel.Writer.TryWrite(new ChangeVisitorRoomWork(userId, userCode, oldControllerIds));

    public int EnqueueMany(IEnumerable<Guid> userIds)
    {
        var count = 0;
        foreach (var id in userIds)
            if (EnqueueSyncCore(id)) count++;
        return count;
    }

    /// <summary>
    /// Chamado pelo worker ao TERMINAR de processar um usuário: se surgiu um pedido de re-sync durante
    /// o processamento (rerun), reenfileira uma vez; senão, libera o usuário.
    /// </summary>
    internal void CompleteSync(Guid userId)
    {
        while (true)
        {
            if (!_active.TryGetValue(userId, out var rerun)) return;
            if (rerun)
            {
                if (_active.TryUpdate(userId, false, comparisonValue: true))
                {
                    _channel.Writer.TryWrite(new SyncUserWork(userId));
                    return;
                }
            }
            else if (_active.TryRemove(new KeyValuePair<Guid, bool>(userId, false)))
            {
                return;
            }
            // Estado mudou concorrentemente entre a leitura e a escrita — tenta de novo.
        }
    }
}
