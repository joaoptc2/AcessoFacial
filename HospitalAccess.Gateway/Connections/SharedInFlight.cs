using System.Collections.Concurrent;

namespace HospitalAccess.Gateway.Connections;

/// <summary>
/// Uma execução por chave de cada vez, COMPARTILHADA: quem chega enquanto a operação daquela chave
/// está em andamento recebe o resultado dela, em vez de disparar a sua própria. Terminada, a
/// chave é liberada — o resultado não fica guardado, a próxima chamada executa de novo.
///
/// Nasceu da sonda de prova de vida. Em nov/2026, cada worker da sincronização fazia a sua sonda
/// na mesma porta morta (Quarto 9): elas entravam na fila do controlador uma atrás da outra, a
/// primeira falhava em 13,5 s, e as demais esperavam esse tempo só para serem recusadas pelo
/// disjuntor. Chegaram a ser 6 dos 8 workers parados ao mesmo tempo — 2,2 h no período — enquanto
/// cadastros para portas boas aguardavam.
/// </summary>
public sealed class SharedInFlight<TKey, TResult> where TKey : notnull
{
    private readonly ConcurrentDictionary<TKey, Task<TResult>> _inFlight = new();

    /// <summary>Execuções em andamento agora (diagnóstico e testes).</summary>
    public int Count => _inFlight.Count;

    /// <summary>
    /// Executa <paramref name="work"/> para a chave, ou junta-se à execução em andamento.
    /// <paramref name="ct"/> só interrompe a ESPERA de quem chamou: a execução compartilhada
    /// segue até o fim para os demais.
    /// </summary>
    public Task<TResult> RunAsync(TKey key, Func<Task<TResult>> work, CancellationToken ct = default)
    {
        var mine = new TaskCompletionSource<TResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var current = _inFlight.GetOrAdd(key, mine.Task);
        if (current == mine.Task)
            _ = ExecuteAsync(key, work, mine);
        return current.WaitAsync(ct);
    }

    private async Task ExecuteAsync(TKey key, Func<Task<TResult>> work, TaskCompletionSource<TResult> completion)
    {
        try
        {
            completion.TrySetResult(await work());
        }
        catch (OperationCanceledException ex)
        {
            completion.TrySetCanceled(ex.CancellationToken);
        }
        catch (Exception ex)
        {
            completion.TrySetException(ex);
        }
        finally
        {
            // Só remove a PRÓPRIA entrada: uma execução nova da mesma chave não pode ser apagada.
            _inFlight.TryRemove(new KeyValuePair<TKey, Task<TResult>>(key, completion.Task));
        }
    }
}
