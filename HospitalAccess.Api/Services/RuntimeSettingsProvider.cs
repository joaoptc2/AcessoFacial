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
        string WelcomeService, string ClearService, int TimeoutSeconds)
    {
        /// <summary>Pronto para uso: ligado E com URL e token resolvidos.</summary>
        public bool Ready => Enabled && !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(Token);
    }

    public sealed record WelcomeSettings(string BaseImagePath, string OutputDirectory, string PublicBaseUrl,
        int TextX, int TextY, bool CenterHorizontally, float FontSize, string FontColorHex, string FontPath);

    private sealed record Snapshot(HomeAssistantSettings HomeAssistant, WelcomeSettings Welcome, string DeviceDefaultPassword);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly HomeAssistantOptions _haDefaults;
    private readonly BedManagementOptions _bedDefaults;
    private readonly DeviceHttpOptions _deviceHttpDefaults;
    private readonly ILogger<RuntimeSettingsProvider> _logger;
    private volatile Snapshot? _current;
    // Versão bumpada a cada Invalidate: um Reload que começou ANTES de um salvamento não pode
    // cachear seu snapshot obsoleto por cima da invalidação (corrida Invalidate × Reload).
    private int _version;

    public RuntimeSettingsProvider(IServiceScopeFactory scopeFactory, IOptions<HomeAssistantOptions> haDefaults,
        IOptions<BedManagementOptions> bedDefaults, IOptions<DeviceHttpOptions> deviceHttpDefaults,
        ILogger<RuntimeSettingsProvider> logger)
    {
        _scopeFactory = scopeFactory;
        _haDefaults = haDefaults.Value;
        _bedDefaults = bedDefaults.Value;
        _deviceHttpDefaults = deviceHttpDefaults.Value;
        _logger = logger;
    }

    public HomeAssistantSettings HomeAssistant => Current.HomeAssistant;
    public WelcomeSettings Welcome => Current.Welcome;

    /// <summary>Senha de comunicação padrão dos aparelhos (a global única das Configurações).</summary>
    public string? DefaultCommunicationPassword => NonEmpty(Current.DeviceDefaultPassword);

    /// <summary>Senha padrão do painel web: a global única, senão a do appsettings (Device:DefaultApiPassword).</summary>
    public string? DefaultApiPassword => NonEmpty(Current.DeviceDefaultPassword) ?? NonEmpty(_deviceHttpDefaults.DefaultApiPassword);

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
            _bedDefaults.FontPath),
        row?.DeviceDefaultPassword ?? string.Empty);

    private static string Pick(string? fromDb, string fallback) =>
        string.IsNullOrWhiteSpace(fromDb) ? fallback : fromDb;

    private static string? NonEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
