using System.Collections.Concurrent;

namespace HospitalAccess.Api.Services;

/// <summary>
/// Guarda de reentrância para operações pesadas disparadas por endpoint e executadas em segundo
/// plano (resync total de um controlador, sync-all de feriados/grades). Sem ela, cliques
/// repetidos empilhavam execuções concorrentes da MESMA operação — no pior caso vários
/// ClearAllPersons + re-upload completo simultâneos no mesmo aparelho.
/// Uso: if (!singleFlight.TryBegin(chave)) return Conflict(...); e End(chave) no finally do job.
/// </summary>
public sealed class SingleFlight
{
    private readonly ConcurrentDictionary<string, byte> _running = new();

    /// <summary>Tenta iniciar a operação; false = já existe uma execução em andamento com esta chave.</summary>
    public bool TryBegin(string key) => _running.TryAdd(key, 0);

    /// <summary>Libera a chave ao término (sempre em finally).</summary>
    public void End(string key) => _running.TryRemove(key, out _);
}
