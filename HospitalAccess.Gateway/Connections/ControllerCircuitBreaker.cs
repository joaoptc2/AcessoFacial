namespace HospitalAccess.Gateway.Connections;

/// <summary>
/// Disjuntor de comandos POR CONTROLADOR. Visto em produção: um aparelho degradado (lento a
/// ponto de nem o próprio painel web abrir) continuava recebendo tentativas de upload de face
/// (~120 KB) de vários usuários — o backoff é por usuário×porta, então o aparelho doente ainda
/// era martelado várias vezes por janela, o que só piorava o estado dele.
///
/// Semântica:
/// - <see cref="FailureThreshold"/> falhas CONSECUTIVAS → abre por um cooldown exponencial
///   (base 1 min, dobra a cada reabertura, teto 15 min). Aberto = comandos falham rápido, sem
///   tocar a rede.
/// - Meia-abertura: vencido o cooldown, os comandos voltam a fluir, mas UMA falha já reabre
///   (com cooldown maior) — não são necessárias mais 3 marteladas no aparelho doente.
/// - Qualquer sucesso fecha e zera tudo.
/// Thread-safe; o relógio entra por parâmetro (testável).
/// </summary>
public sealed class ControllerCircuitBreaker
{
    public const int FailureThreshold = 3;
    public static readonly TimeSpan BaseCooldown = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan MaxCooldown = TimeSpan.FromMinutes(15);

    private readonly object _lock = new();
    private int _consecutiveFailures;
    private int _openCount;
    private DateTime? _openUntilUtc;

    /// <summary>Se o circuito está aberto em <paramref name="nowUtc"/>, devolve até quando; senão null.</summary>
    public DateTime? OpenUntil(DateTime nowUtc)
    {
        lock (_lock)
        {
            return _openUntilUtc is { } until && until > nowUtc ? until : null;
        }
    }

    /// <summary>
    /// A porta provou que está viva desde a última falha? Verdadeiro só depois de um sucesso e
    /// antes de qualquer nova falha — falso com falhas abaixo do limiar, com o circuito aberto e
    /// também na meia-abertura (cooldown vencido, mas nenhum sucesso desde então).
    ///
    /// É a pergunta certa para decidir se vale mandar trabalho PESADO a uma porta. "O circuito
    /// está aberto?" não basta: vencido o cooldown ele deixa de estar aberto sem que a porta tenha
    /// respondido nada, e foi nessa janela que, na medição de nov/2026, sete uploads de face de
    /// 22,5 s cada entravam juntos numa porta morta.
    /// </summary>
    public bool IsHealthy
    {
        get
        {
            lock (_lock)
            {
                // Ao abrir, o contador de falhas é zerado — quem guarda a memória da abertura é
                // _openCount, que só um sucesso zera.
                return _consecutiveFailures == 0 && _openCount == 0;
            }
        }
    }

    /// <summary>Comando confirmado pelo aparelho: fecha o circuito e zera o histórico.</summary>
    public void RecordSuccess()
    {
        lock (_lock)
        {
            _consecutiveFailures = 0;
            _openCount = 0;
            _openUntilUtc = null;
        }
    }

    /// <summary>
    /// Comando falhou (timeout/erro de protocolo). Devolve até quando o circuito ficou aberto,
    /// se esta falha o abriu/reabriu; senão null.
    /// </summary>
    public DateTime? RecordFailure(DateTime nowUtc)
    {
        lock (_lock)
        {
            _consecutiveFailures++;
            // Meia-abertura: se já abriu antes (sem sucesso desde então), 1 falha reabre direto.
            var threshold = _openCount > 0 ? 1 : FailureThreshold;
            if (_consecutiveFailures < threshold) return null;

            _openCount++;
            var cooldownMs = Math.Min(
                BaseCooldown.TotalMilliseconds * Math.Pow(2, _openCount - 1),
                MaxCooldown.TotalMilliseconds);
            _openUntilUtc = nowUtc + TimeSpan.FromMilliseconds(cooldownMs);
            _consecutiveFailures = 0;
            return _openUntilUtc;
        }
    }
}
