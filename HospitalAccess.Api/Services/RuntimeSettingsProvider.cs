using HospitalAccess.Api.Options;
using HospitalAccess.Gateway;
using HospitalAccess.Infrastructure.Devices;
using HospitalAccess.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HospitalAccess.Api.Services;

/// <summary>
/// Valores EFETIVOS das configurações administráveis pela tela: o que estiver preenchido na
/// linha de SystemSettings vence; vazio/null herda o appsettings (migração suave — ambientes
/// configurados por arquivo continuam funcionando sem tocar em nada). Snapshot em memória,
/// recarregado no primeiro uso e sempre que o SettingsController salva (<see cref="Invalidate"/>)
/// — os consumidores leem POR CHAMADA, então mudar a configuração NÃO exige restart.
/// </summary>
public sealed class RuntimeSettingsProvider : IDeviceSecretDefaults, IDeviceHttpSecretDefaults
{
    public sealed record HomeAssistantSettings(bool Enabled, string BaseUrl, string Token,
        string WelcomeService, string ClearService, string ExtraActionService, string ExtraActionLabel,
        int TimeoutSeconds)
    {
        /// <summary>Pronto para uso: ligado E com URL e token resolvidos.</summary>
        public bool Ready => Enabled && !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(Token);

        /// <summary>A ação extra só existe se houver serviço configurado (o rótulo sempre tem padrão).</summary>
        public bool HasExtraAction => !string.IsNullOrWhiteSpace(ExtraActionService);
    }

    /// <summary>
    /// Política de cópias de segurança. Lida POR CICLO pela rotina automática, então mudar na
    /// tela vale na próxima cópia — sem reiniciar o serviço.
    /// </summary>
    public sealed record BackupSettings(int IntervalHours, int MaxFiles, int RetentionDays);

    /// <summary>Diagnóstico de desempenho dos aparelhos (ver DeviceTraceRecorder).</summary>
    public sealed record DiagnosticsSettings(bool DeviceTraceEnabled, int DeviceTraceRetentionDays);

    public sealed record WelcomeSettings(string BaseImagePath, string OutputDirectory, string PublicBaseUrl,
        int TextX, int TextY, bool CenterHorizontally, float FontSize, string FontColorHex, string FontPath);

    private sealed record Snapshot(HomeAssistantSettings HomeAssistant, WelcomeSettings Welcome,
        BackupSettings Backup, DiagnosticsSettings Diagnostics,
        string DeviceDefaultCommunicationPassword, string DeviceDefaultApiPassword);

    /// <summary>Padrões de FÁBRICA do 8190H — último recurso quando nem o banco nem o appsettings definem.</summary>
    public const string FactoryCommunicationPassword = "FFFFFFFF";
    public const string FactoryApiPassword = "1409";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly HomeAssistantOptions _haDefaults;
    private readonly BedManagementOptions _bedDefaults;
    private readonly BackupOptions _backupDefaults;
    private readonly DeviceHttpOptions _deviceHttpDefaults;
    private readonly ILogger<RuntimeSettingsProvider> _logger;
    private volatile Snapshot? _current;
    // Versão bumpada a cada Invalidate: um Reload que começou ANTES de um salvamento não pode
    // cachear seu snapshot obsoleto por cima da invalidação (corrida Invalidate × Reload).
    private int _version;

    public RuntimeSettingsProvider(IServiceScopeFactory scopeFactory, IOptions<HomeAssistantOptions> haDefaults,
        IOptions<BedManagementOptions> bedDefaults, IOptions<DeviceHttpOptions> deviceHttpDefaults,
        IOptions<BackupOptions> backupDefaults, ILogger<RuntimeSettingsProvider> logger)
    {
        _scopeFactory = scopeFactory;
        _haDefaults = haDefaults.Value;
        _bedDefaults = bedDefaults.Value;
        _backupDefaults = backupDefaults.Value;
        _deviceHttpDefaults = deviceHttpDefaults.Value;
        _logger = logger;
    }

    public HomeAssistantSettings HomeAssistant => Current.HomeAssistant;
    public WelcomeSettings Welcome => Current.Welcome;
    public BackupSettings Backup => Current.Backup;
    public DiagnosticsSettings Diagnostics => Current.Diagnostics;

    /// <summary>Senha de comunicação padrão: Configurações → padrão de fábrica (FFFFFFFF).</summary>
    public string? DefaultCommunicationPassword =>
        NonEmpty(Current.DeviceDefaultCommunicationPassword) ?? FactoryCommunicationPassword;

    /// <summary>Senha padrão do painel web: Configurações → appsettings (Device:DefaultApiPassword) → padrão de fábrica (1409).</summary>
    public string? DefaultApiPassword =>
        NonEmpty(Current.DeviceDefaultApiPassword)
        ?? NonEmpty(_deviceHttpDefaults.DefaultApiPassword)
        ?? FactoryApiPassword;

    /// <summary>Descarta o snapshot — o próximo acesso relê do banco (chamado pelo SettingsController ao salvar).</summary>
    public void Invalidate()
    {
        Interlocked.Increment(ref _version);
        _current = null;
    }

    private Snapshot Current => _current ?? Reload();

    private Snapshot Reload()
    {
        var versionAtStart = Volatile.Read(ref _version);
        Domain.Entities.SystemSettings? row = null;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AccessDbContext>();
            row = db.SystemSettings.AsNoTracking()
                .FirstOrDefault(s => s.Id == Domain.Entities.SystemSettings.SingletonId);
        }
        catch (Exception ex)
        {
            // Banco indisponível (boot, manutenção): opera com os defaults do appsettings e NÃO
            // cacheia — o próximo acesso tenta de novo.
            _logger.LogWarning(ex, "Não foi possível ler as configurações do banco — usando os valores do appsettings.");
            return Compose(null);
        }

        var snapshot = Compose(row);
        // Só cacheia se nenhum Invalidate aconteceu durante a leitura (snapshot ainda atual).
        if (Volatile.Read(ref _version) == versionAtStart)
            _current = snapshot;
        return snapshot;
    }

    private Snapshot Compose(Domain.Entities.SystemSettings? row) => new(
        new HomeAssistantSettings(
            row?.HomeAssistantEnabled ?? _haDefaults.Enabled,
            Pick(row?.HomeAssistantBaseUrl, _haDefaults.BaseUrl),
            Pick(row?.HomeAssistantToken, _haDefaults.Token),
            Pick(row?.HomeAssistantWelcomeService, _haDefaults.WelcomeService),
            Pick(row?.HomeAssistantClearService, _haDefaults.ClearService),
            Pick(row?.HomeAssistantExtraActionService, _haDefaults.ExtraActionService),
            // Dois níveis de fallback: o rótulo é o TEXTO do botão, então não pode chegar vazio
            // à tela nem quando o appsettings traz a chave em branco.
            Pick(row?.HomeAssistantExtraActionLabel,
                Pick(_haDefaults.ExtraActionLabel, HomeAssistantOptions.DefaultExtraActionLabel)),
            _haDefaults.TimeoutSeconds),
        new WelcomeSettings(
            Pick(row?.WelcomeBaseImagePath, _bedDefaults.WelcomeBaseImagePath),
            _bedDefaults.WelcomeOutputDirectory, // montado como /welcome/* no boot — só appsettings
            Pick(row?.WelcomePublicBaseUrl, _bedDefaults.PublicBaseUrl),
            _bedDefaults.TextX,
            row?.WelcomeTextY ?? _bedDefaults.TextY,
            _bedDefaults.CenterHorizontally,
            row?.WelcomeFontSize ?? _bedDefaults.FontSize,
            Pick(row?.WelcomeFontColorHex, _bedDefaults.FontColorHex),
            Pick(row?.WelcomeFontPath, _bedDefaults.FontPath)),
        new BackupSettings(
            // Piso de 1h: um intervalo 0 no banco faria a rotina girar em laço fechado.
            Math.Max(1, row?.BackupIntervalHours ?? _backupDefaults.IntervalHours),
            Math.Max(0, row?.BackupMaxFiles ?? _backupDefaults.MaxFiles),
            _backupDefaults.RetentionDays),
        new DiagnosticsSettings(
            row?.DeviceTraceEnabled ?? false,
            // Piso de 1 dia: prazo 0 apagaria a coleta na primeira passagem do expurgo.
            Math.Max(1, row?.DeviceTraceRetentionDays ?? 7)),
        row?.DeviceDefaultCommunicationPassword ?? string.Empty,
        row?.DeviceDefaultApiPassword ?? string.Empty);

    private static string Pick(string? fromDb, string fallback) =>
        string.IsNullOrWhiteSpace(fromDb) ? fallback : fromDb;

    private static string? NonEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
