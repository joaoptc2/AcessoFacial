namespace HospitalAccess.Application.Diagnostics;

/// <summary>Uma medição pronta para gravar. Imutável: sai do caminho do comando e não volta.</summary>
public sealed record DeviceCommandTraceRecord(
    DateTime StartedAtUtc,
    Guid ControllerId,
    string ControllerName,
    string ControllerIp,
    string Channel,
    string Operation,
    string Trigger,
    int QueueWaitMs,
    int DurationMs,
    int QueueDepth,
    string Outcome,
    string? Error,
    int PayloadBytes,
    int TimeoutMs,
    int RestartCount,
    long? UserCode);

/// <summary>Desfechos possíveis. Constantes, e não enum, porque viajam para o CSV como texto.</summary>
public static class DeviceTraceOutcome
{
    public const string Success = "Success";
    public const string Failed = "Failed";
    public const string Timeout = "Timeout";
    public const string CircuitOpen = "CircuitOpen";
    public const string GateBusy = "GateBusy";
    public const string Canceled = "Canceled";
}

/// <summary>De onde partiu o comando. Ver <see cref="DeviceTraceContext"/>.</summary>
public static class DeviceTraceTrigger
{
    public const string SyncQueue = "fila-sincronizacao";
    public const string ManualCommand = "comando-manual";
    public const string BedManagement = "gestao-leitos";
    public const string HealthCheck = "health-check";
    public const string Monitoring = "monitoramento";
    public const string Unknown = "desconhecido";
}

/// <summary>
/// O gatilho corrente, propagado pelo fluxo assíncrono. É AsyncLocal em vez de parâmetro porque
/// são 60 pontos de chamada no gateway: enfiar um parâmetro em todos eles só para o diagnóstico
/// contaminaria as assinaturas de produção para sempre.
/// </summary>
public static class DeviceTraceContext
{
    private static readonly AsyncLocal<string?> Current = new();

    public static string Trigger => Current.Value ?? DeviceTraceTrigger.Unknown;

    /// <summary>Marca o gatilho até o descarte. Use com <c>using</c> em volta do bloco que comanda aparelhos.</summary>
    public static IDisposable Use(string trigger)
    {
        var previous = Current.Value;
        Current.Value = trigger;
        return new Scope(previous);
    }

    private sealed class Scope(string? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}

/// <summary>
/// Destino das medições. A implementação PRECISA retornar na hora (enfileirar em memória): se
/// gravar no banco dentro do comando, o próprio instrumento vira parte do que está sendo medido.
/// </summary>
public interface IDeviceTraceSink
{
    /// <summary>Ligado? Quando falso, quem chama nem monta o registro.</summary>
    bool Enabled { get; }

    /// <summary>Enfileira. Nunca lança, nunca bloqueia: diagnóstico não derruba operação.</summary>
    void Record(DeviceCommandTraceRecord trace);
}

/// <summary>Destino nulo: usado quando o diagnóstico está desligado ou em teste.</summary>
public sealed class NullDeviceTraceSink : IDeviceTraceSink
{
    public bool Enabled => false;
    public void Record(DeviceCommandTraceRecord trace) { }
}
