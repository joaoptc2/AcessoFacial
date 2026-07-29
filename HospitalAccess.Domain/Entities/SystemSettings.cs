namespace HospitalAccess.Domain.Entities;

/// <summary>
/// Configurações globais do sistema (linha única). Hoje concentra as políticas de retenção de
/// dados (LGPD), configuráveis pela tela de Configurações. Um valor 0 significa "reter
/// indefinidamente" (sem expurgo automático).
/// </summary>
public class SystemSettings
{
    /// <summary>Chave fixa da linha única de configuração.</summary>
    public static readonly Guid SingletonId = new("00000000-0000-0000-0000-0000000000AA");

    public Guid Id { get; set; } = SingletonId;

    /// <summary>Dias para reter fotos de evento capturadas pelos controladores. 0 = nunca expurgar.</summary>
    public int EventPhotoRetentionDays { get; set; } = 90;

    /// <summary>Dias para reter o log de acessos. 0 = nunca expurgar (padrão: auditoria hospitalar exige guarda longa).</summary>
    public int AccessLogRetentionDays { get; set; }

    /// <summary>Dias para reter o log de alarmes. 0 = nunca expurgar.</summary>
    public int AlarmLogRetentionDays { get; set; }

    /// <summary>Dias para reter a trilha de auditoria de comandos de porta. 0 = nunca expurgar.</summary>
    public int ControllerAuditRetentionDays { get; set; }

    /// <summary>
    /// Formato do QR de acesso:
    /// - "PlainText" (padrão): Base64 de "user_id={code}_time={microssegundos Unix}". É o formato
    ///   CONFIRMADO contra o QR real do sistema oficial do fabricante (idêntico byte a byte).
    /// - "Appendix8Rc4": formato binário do Apêndice 8 (cifrado com RC4) — alternativa para
    ///   firmwares que exijam o formato documentado.
    /// Selecionável porque firmwares diferentes aceitam formatos diferentes.
    /// </summary>
    public string QrFormat { get; set; } = "PlainText";

    /// <summary>
    /// MODO DE DESENVOLVIMENTO: quando ativo, os logs importantes (Information+ do sistema,
    /// Warning+ do framework) são espelhados num buffer em memória e exibidos na tela
    /// "Logs (Dev)". Persistido para sobreviver a restart; o custo com o modo desligado é zero.
    /// </summary>
    public bool DevelopmentModeEnabled { get; set; }

    // ------------------------------------------------------------------------------------------
    // Configurações administráveis pela TELA (sem linha de comando). Semântica de herança:
    // valor vazio/null = vale o que estiver no appsettings (migração suave — ambientes já
    // configurados por arquivo continuam funcionando). Ver RuntimeSettingsProvider.
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// Senha de COMUNICAÇÃO padrão dos aparelhos (protocolo binário — padrão de fábrica
    /// "FFFFFFFF"). Criptografada em repouso. Controlador com senha própria tem precedência.
    /// </summary>
    public string DeviceDefaultCommunicationPassword { get; set; } = string.Empty;

    /// <summary>
    /// Senha padrão do PAINEL WEB dos aparelhos (padrão de fábrica "1409"). Criptografada em
    /// repouso. Controlador com senha própria tem precedência; vazio herda o appsettings
    /// (Device:DefaultApiPassword) e, em último caso, o padrão de fábrica.
    /// </summary>
    public string DeviceDefaultApiPassword { get; set; } = string.Empty;

    /// <summary>Home Assistant: null = herda do appsettings; true/false = decisão explícita da tela.</summary>
    public bool? HomeAssistantEnabled { get; set; }
    public string HomeAssistantBaseUrl { get; set; } = string.Empty;
    /// <summary>Long-lived access token do HA. Criptografado em repouso.</summary>
    public string HomeAssistantToken { get; set; } = string.Empty;
    public string HomeAssistantWelcomeService { get; set; } = string.Empty;
    public string HomeAssistantClearService { get; set; } = string.Empty;

    /// <summary>Tela de boas-vindas (gestão de leitos): imagem base e composição do texto.</summary>
    public string WelcomeBaseImagePath { get; set; } = string.Empty;
    public string WelcomePublicBaseUrl { get; set; } = string.Empty;
    public int? WelcomeTextY { get; set; }
    public float? WelcomeFontSize { get; set; }
    public string WelcomeFontColorHex { get; set; } = string.Empty;

    /// <summary>Última atualização das configurações.</summary>
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Username que atualizou as configurações por último.</summary>
    public string? UpdatedByUsername { get; set; }
}
