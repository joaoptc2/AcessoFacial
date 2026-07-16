namespace HospitalAccess.Api.Options;

// Opções operacionais dos serviços que conversam com os controladores. Todos os defaults
// reproduzem o comportamento anterior (valores que antes eram constantes hardcoded) — um
// appsettings vazio não muda nada; ver appsettings.example.json para o racional de cada chave.

/// <summary>Retry automático de sincronização (backoff exponencial por porta).</summary>
public sealed class SyncRetryOptions
{
    public const string SectionName = "Sync";

    /// <summary>Intervalo da varredura que reenfileira sincronizações elegíveis (segundos).</summary>
    public int ScanIntervalSeconds { get; set; } = 120;

    /// <summary>Base do backoff exponencial (minutos): 1ª falha re-tenta após este tempo.</summary>
    public double BackoffBaseMinutes { get; set; } = 2;

    /// <summary>Fator multiplicativo do backoff a cada falha consecutiva.</summary>
    public double BackoffFactor { get; set; } = 2;

    /// <summary>Teto do backoff (minutos): falhas transitórias persistentes assentam neste ritmo.</summary>
    public double BackoffCapMinutes { get; set; } = 60;

    /// <summary>
    /// Reenvios NO FIO (SDK) por upload de face. O SDK re-emite cada comando este número de
    /// vezes antes de reportar falha; com o backoff da aplicação cuidando dos retries, 1 evita
    /// pagar o payload de ~120 KB em triplicata a cada falha (o default 3 do SDK permanece para
    /// os comandos leves).
    /// </summary>
    public int FaceUploadWireRetries { get; set; } = 1;
}

/// <summary>Fila de sincronização com o hardware.</summary>
public sealed class SyncQueueOptions
{
    public const string SectionName = "SyncQueue";

    /// <summary>Workers paralelos (a serialização real é por controlador, no gateway).</summary>
    public int WorkerCount { get; set; } = 8;
}

/// <summary>Manutenção do monitoramento em tempo real (BeginWatch).</summary>
public sealed class MonitoringOptions
{
    public const string SectionName = "Monitoring";

    /// <summary>Intervalo do ciclo de re-arme (minutos).</summary>
    public int RearmIntervalMinutes { get; set; } = 5;

    /// <summary>
    /// Janela de "push saudável" (minutos): controlador com atividade de push (evento ou
    /// keepalive) dentro dela é pulado no re-arme — zero tráfego em regime permanente.
    /// </summary>
    public int PushIdleThresholdMinutes { get; set; } = 10;
}

/// <summary>Coleta de retaguarda dos registros offline.</summary>
public sealed class OfflineCollectionOptions
{
    public const string SectionName = "OfflineCollection";

    /// <summary>Intervalo entre varreduras (minutos). Em redes carregadas, 60 é recomendado.</summary>
    public int IntervalMinutes { get; set; } = 15;

    /// <summary>
    /// Pular controladores com push recente (janela de <see cref="MonitoringOptions.PushIdleThresholdMinutes"/>).
    /// Default OFF: só ligar após validar em hardware que o push avança o ponteiro de leitura
    /// do aparelho — caso contrário registros podem ficar sem coleta.
    /// A primeira varredura após o boot é sempre completa (recupera o backlog).
    /// </summary>
    public bool SkipWhenPushHealthy { get; set; }
}

/// <summary>Sonda de presença dos controladores (painel de status).</summary>
public sealed class HealthCheckOptions
{
    public const string SectionName = "HealthCheck";

    /// <summary>Intervalo entre ciclos de verificação (segundos).</summary>
    public int IntervalSeconds { get; set; } = 60;

    /// <summary>Timeout de cada sonda individual (segundos).</summary>
    public int ProbeTimeoutSeconds { get; set; } = 3;

    /// <summary>Verificações simultâneas por ciclo.</summary>
    public int MaxParallelChecks { get; set; } = 16;

    /// <summary>
    /// Permite, como ÚLTIMO recurso (ICMP bloqueado e sem ApiBaseUrl), o TCP connect na porta do
    /// SDK/monitoramento. Cada sonda dessas abre e derruba uma conexão na porta de protocolo do
    /// aparelho — prefira liberar ICMP ou configurar ApiBaseUrl e desligar isto.
    /// </summary>
    public bool AllowSdkPortFallback { get; set; } = true;
}
