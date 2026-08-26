namespace HospitalAccess.Api.Services;

/// <summary>Usuário apontado como "Faltando no dispositivo" — enriquecido para a tela oferecer o reenvio individual.</summary>
public sealed record AuditMissingUser(Guid UserId, uint UserCode, string Name, string Type);

/// <summary>Resultado da auditoria de UM controlador. <see cref="Error"/> preenchido = aparelho não respondeu.</summary>
public sealed record PersonnelAuditItem(
    Guid ControllerId,
    string ControllerName,
    string? Error,
    List<AuditMissingUser>? MissingOnDevice,
    List<uint>? ExtraOnDevice,
    int? DeviceCount,
    int? ExpectedCount);

/// <summary>Resultado completo de uma varredura de auditoria.</summary>
public sealed record PersonnelAuditReport(DateTime GeneratedAtUtc, List<PersonnelAuditItem> Results);

public enum PersonnelAuditPhase
{
    /// <summary>Nunca rodou desde a subida do serviço.</summary>
    Idle,
    Running,
    Completed,
    /// <summary>A varredura inteira falhou (ex.: banco fora). Falha de UM aparelho não chega aqui — vira Error no item.</summary>
    Failed,
}

/// <summary>Instantâneo consumido pela tela a cada consulta.</summary>
public sealed record PersonnelAuditSnapshot(
    PersonnelAuditPhase Phase,
    DateTime? StartedAtUtc,
    DateTime? CompletedAtUtc,
    int Done,
    int Total,
    string? Error,
    PersonnelAuditReport? Result);

/// <summary>
/// Estado da auditoria de pessoal de TODOS os controladores, que roda em segundo plano.
///
/// <para>
/// POR QUE EM SEGUNDO PLANO: a varredura lê os 30 aparelhos com concorrência limitada e cada
/// leitura tem teto de 3 minutos. Com vários controladores lentos ou fora do ar, o pior caso é da
/// ordem de 20 minutos — e o Nginx da instalação corta a requisição em 60 s por padrão. A tela
/// falhava exatamente quando era mais necessária: quando há aparelho com problema. Mesmo padrão
/// que o resync-all já usava (dispara, responde 202, consulta depois).
/// </para>
/// <para>
/// Singleton em memória, como o DevLogBuffer: o resultado se perde no restart de propósito — é
/// diagnóstico operacional, não auditoria persistida.
/// </para>
/// </summary>
public sealed class PersonnelAuditState
{
    private readonly object _gate = new();

    private PersonnelAuditPhase _phase = PersonnelAuditPhase.Idle;
    private DateTime? _startedAtUtc;
    private DateTime? _completedAtUtc;
    private int _done;
    private int _total;
    private string? _error;
    private PersonnelAuditReport? _result;

    public PersonnelAuditSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new PersonnelAuditSnapshot(_phase, _startedAtUtc, _completedAtUtc, _done, _total, _error, _result);
        }
    }

    /// <summary>
    /// Marca o início. O resultado ANTERIOR é mantido de propósito: a tela continua mostrando a
    /// última auditoria conhecida enquanto a nova roda, em vez de piscar vazia.
    /// </summary>
    public void MarkRunning(int total)
    {
        lock (_gate)
        {
            _phase = PersonnelAuditPhase.Running;
            _startedAtUtc = DateTime.UtcNow;
            _completedAtUtc = null;
            _error = null;
            _done = 0;
            _total = total;
        }
    }

    public void ReportProgress()
    {
        lock (_gate) { _done++; }
    }

    public void MarkCompleted(PersonnelAuditReport report)
    {
        lock (_gate)
        {
            _phase = PersonnelAuditPhase.Completed;
            _completedAtUtc = DateTime.UtcNow;
            _result = report;
            _done = _total;
        }
    }

    public void MarkFailed(string error)
    {
        lock (_gate)
        {
            _phase = PersonnelAuditPhase.Failed;
            _completedAtUtc = DateTime.UtcNow;
            _error = error;
        }
    }
}
