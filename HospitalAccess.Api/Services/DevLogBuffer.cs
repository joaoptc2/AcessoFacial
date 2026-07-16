using System.Collections.Concurrent;

namespace HospitalAccess.Api.Services;

/// <summary>Uma linha capturada pelo modo de desenvolvimento.</summary>
public sealed class DevLogEntry
{
    public long Id { get; init; }
    public DateTime TimestampUtc { get; init; }
    public string Level { get; init; } = "";
    public string Category { get; init; } = "";
    public string Message { get; init; } = "";
    public string? Exception { get; init; }
}

/// <summary>
/// Buffer circular EM MEMÓRIA dos logs importantes, alimentado pelo <see cref="DevLogLoggerProvider"/>
/// quando o MODO DE DESENVOLVIMENTO está ativo (toggle persistido em SystemSettings, cacheado aqui
/// para custo zero no caminho de log). Guarda as últimas <see cref="Capacity"/> linhas — é uma
/// ferramenta de diagnóstico da tela "Logs (Dev)", não um log de auditoria (os logs de
/// acesso/alarme/auditoria continuam no banco; o log completo do serviço continua no
/// journalctl/console). Nada é gravado em disco e o conteúdo se perde no restart — de propósito.
/// </summary>
public sealed class DevLogBuffer
{
    public const int Capacity = 2000;

    private readonly ConcurrentQueue<DevLogEntry> _entries = new();
    private long _nextId;
    private volatile bool _enabled;

    /// <summary>Modo de desenvolvimento ativo? (checado a cada Log — precisa ser barato).</summary>
    public bool Enabled => _enabled;

    public void SetEnabled(bool enabled) => _enabled = enabled;

    public void Add(string level, string category, string message, Exception? exception)
    {
        _entries.Enqueue(new DevLogEntry
        {
            Id = Interlocked.Increment(ref _nextId),
            TimestampUtc = DateTime.UtcNow,
            Level = level,
            Category = category,
            Message = message,
            Exception = exception?.ToString(),
        });

        // Ring buffer: descarta as mais antigas além da capacidade (best-effort sob concorrência).
        while (_entries.Count > Capacity && _entries.TryDequeue(out _)) { }
    }

    /// <summary>Linhas com Id maior que <paramref name="sinceId"/>, em ordem, até <paramref name="take"/>.</summary>
    public IReadOnlyList<DevLogEntry> Snapshot(long sinceId = 0, int take = 500)
    {
        var result = new List<DevLogEntry>();
        foreach (var entry in _entries) // ConcurrentQueue enumera do mais antigo ao mais novo
        {
            if (entry.Id <= sinceId) continue;
            result.Add(entry);
            if (result.Count >= take) break;
        }
        return result;
    }

    public int Count => _entries.Count;

    public void Clear()
    {
        while (_entries.TryDequeue(out _)) { }
    }
}

/// <summary>
/// Provider de logging que espelha para o <see cref="DevLogBuffer"/> os logs IMPORTANTES quando o
/// modo de desenvolvimento está ativo: Information+ das categorias do próprio sistema
/// (HospitalAccess.*) e Warning+ do resto (framework, EF etc. — o ruído de Information deles não
/// interessa). Não substitui os providers normais (console/journal) — é um espelho adicional.
/// </summary>
public sealed class DevLogLoggerProvider : ILoggerProvider
{
    private readonly DevLogBuffer _buffer;

    public DevLogLoggerProvider(DevLogBuffer buffer) => _buffer = buffer;

    public ILogger CreateLogger(string categoryName) => new DevLogLogger(_buffer, categoryName);

    public void Dispose() { }

    private sealed class DevLogLogger : ILogger
    {
        private readonly DevLogBuffer _buffer;
        private readonly string _category;
        private readonly LogLevel _minLevel;

        public DevLogLogger(DevLogBuffer buffer, string category)
        {
            _buffer = buffer;
            _category = category;
            _minLevel = category.StartsWith("HospitalAccess", StringComparison.Ordinal)
                ? LogLevel.Information
                : LogLevel.Warning;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= _minLevel && _buffer.Enabled;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            _buffer.Add(logLevel.ToString(), _category, formatter(state, exception), exception);
        }
    }
}
