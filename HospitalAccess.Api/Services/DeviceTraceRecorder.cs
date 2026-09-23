using System.Threading.Channels;
using HospitalAccess.Application.Diagnostics;
using HospitalAccess.Domain.Entities;
using HospitalAccess.Infrastructure.Persistence;

namespace HospitalAccess.Api.Services;

/// <summary>
/// Recebe as medições no caminho do comando e as grava FORA dele.
///
/// Duas regras que não podem ser quebradas, porque violá-las corrompe a própria medição:
/// 1. <see cref="Record"/> retorna na hora. Nada de banco, nada de lock, nada de await — se o
///    instrumento esperar, ele passa a fazer parte do tempo que está medindo.
/// 2. A fila é LIMITADA e descarta quando cheia. Numa tempestade de comandos, perder amostras é
///    aceitável; segurar um comando de porta para gravar diagnóstico não é. O que foi descartado
///    é contado e sai no resumo, para ninguém analisar um CSV com buraco achando que está inteiro.
/// </summary>
public sealed class DeviceTraceRecorder : BackgroundService, IDeviceTraceSink
{
    /// <summary>Teto da fila em memória. ~100 bytes por registro: 20 mil ≈ 2 MB no pior caso.</summary>
    private const int QueueCapacity = 20_000;

    /// <summary>Linhas por INSERT. Lote grande demais segura a transação; pequeno demais multiplica ida e volta.</summary>
    private const int BatchSize = 200;

    /// <summary>Mesmo sem encher o lote, esvazia neste intervalo — o CSV tem que refletir o que acabou de acontecer.</summary>
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(5);

    private readonly Channel<DeviceCommandTraceRecord> _queue =
        Channel.CreateBounded<DeviceCommandTraceRecord>(new BoundedChannelOptions(QueueCapacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropWrite,
        });

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly RuntimeSettingsProvider _settings;
    private readonly ILogger<DeviceTraceRecorder> _logger;
    private long _dropped;
    private long _written;

    public DeviceTraceRecorder(IServiceScopeFactory scopeFactory, RuntimeSettingsProvider settings,
        ILogger<DeviceTraceRecorder> logger)
    {
        _scopeFactory = scopeFactory;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>Lido POR CHAMADA: ligar/desligar na tela vale imediatamente, sem reiniciar o serviço.</summary>
    public bool Enabled => _settings.Diagnostics.DeviceTraceEnabled;

    /// <summary>Amostras perdidas por fila cheia desde o boot.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>Amostras efetivamente gravadas desde o boot.</summary>
    public long Written => Interlocked.Read(ref _written);

    public void Record(DeviceCommandTraceRecord trace)
    {
        if (!_queue.Writer.TryWrite(trace))
            Interlocked.Increment(ref _dropped);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var buffer = new List<DeviceCommandTraceRecord>(BatchSize);
        using var timer = new PeriodicTimer(FlushInterval);

        // Duas fontes de "hora de gravar": lote cheio ou o relógio. O laço lê o que houver sem
        // bloquear e dorme no timer — assim um aparelho parado não gasta CPU girando.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                while (buffer.Count < BatchSize && _queue.Reader.TryRead(out var trace))
                    buffer.Add(trace);

                if (buffer.Count > 0)
                    await FlushAsync(buffer, stoppingToken);

                if (buffer.Count == 0 && !await timer.WaitForNextTickAsync(stoppingToken))
                    break;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Diagnóstico com defeito não pode derrubar o serviço nem virar laço de erro.
                _logger.LogWarning(ex, "Falha ao gravar o rastreamento de comandos — o lote foi descartado.");
                buffer.Clear();
                try { await Task.Delay(FlushInterval, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }

        // Desligamento: grava o que sobrou, sem o token cancelado (senão o último lote se perde).
        while (_queue.Reader.TryRead(out var pending) && buffer.Count < BatchSize)
            buffer.Add(pending);
        if (buffer.Count > 0)
        {
            try { await FlushAsync(buffer, CancellationToken.None); }
            catch (Exception ex) { _logger.LogWarning(ex, "Falha ao gravar o último lote do rastreamento."); }
        }
    }

    private async Task FlushAsync(List<DeviceCommandTraceRecord> buffer, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AccessDbContext>();
        db.DeviceCommandTraces.AddRange(buffer.Select(t => new DeviceCommandTrace
        {
            StartedAtUtc = t.StartedAtUtc,
            ControllerId = t.ControllerId,
            ControllerName = t.ControllerName,
            ControllerIp = t.ControllerIp,
            Channel = t.Channel,
            Operation = t.Operation,
            Trigger = t.Trigger,
            QueueWaitMs = t.QueueWaitMs,
            DurationMs = t.DurationMs,
            QueueDepth = t.QueueDepth,
            Outcome = t.Outcome,
            Error = t.Error,
            PayloadBytes = t.PayloadBytes,
            TimeoutMs = t.TimeoutMs,
            RestartCount = t.RestartCount,
            UserCode = t.UserCode,
        }));
        await db.SaveChangesAsync(ct);
        Interlocked.Add(ref _written, buffer.Count);
        buffer.Clear();
    }
}
