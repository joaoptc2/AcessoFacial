namespace HospitalAccess.Api.Options;

/// <summary>
/// Integração com o Home Assistant (mesma rede) via REST API + long-lived access token.
/// Desabilitada por padrão: com Enabled=false, o módulo de leitos funciona normalmente e só
/// pula as chamadas ao HA. Toda chamada é BEST-EFFORT — falha loga Warning e nunca bloqueia
/// o fluxo de internação/transferência/alta.
/// </summary>
public sealed class HomeAssistantOptions
{
    public const string SectionName = "HomeAssistant";

    public bool Enabled { get; set; }

    /// <summary>Ex.: "http://homeassistant.local:8123" (sem barra final).</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Long-lived access token (perfil do usuário no HA → Segurança). Vem de secret/env: HomeAssistant__Token.</summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>
    /// Serviço chamado nas boas-vindas, formato "domínio.serviço" (ex.: "script.boas_vindas_leito").
    /// Recebe { room, patient_name, welcome_image_url }.
    /// </summary>
    public string WelcomeService { get; set; } = "script.boas_vindas_leito";

    /// <summary>Serviço opcional chamado na alta/transferência para limpar a TV do quarto ({ room }). Vazio = não chama.</summary>
    public string ClearService { get; set; } = string.Empty;

    public int TimeoutSeconds { get; set; } = 5;
}

/// <summary>Módulo de gestão de leitos: acesso do paciente e tela de boas-vindas.</summary>
public sealed class BedManagementOptions
{
    public const string SectionName = "BedManagement";

    /// <summary>Validade padrão (horas) do acesso criado na internação. 0 = sem validade automática.</summary>
    public int DefaultStayDurationHours { get; set; } = 24 * 7;

    /// <summary>Grupo de horário (1-64) do acesso do paciente.</summary>
    public int PatientTimeGroup { get; set; } = 1;

    /// <summary>Imagem base (JPG/PNG) da tela de boas-vindas fornecida pelo hospital.</summary>
    public string WelcomeBaseImagePath { get; set; } = string.Empty;

    /// <summary>
    /// Diretório onde os JPGs gerados são salvos e servidos publicamente em /welcome/*.
    /// Fora do wwwroot de propósito (o build do front pode limpá-lo). Precisa ser gravável
    /// pelo usuário do serviço.
    /// </summary>
    public string WelcomeOutputDirectory { get; set; } = "/var/lib/hospitalaccess/welcome";

    /// <summary>
    /// Endereço deste servidor ALCANÇÁVEL PELO HA (ex.: "http://192.168.19.10"), usado para
    /// compor a welcome_image_url. Vazio = envia caminho relativo (/welcome/...).
    /// </summary>
    public string PublicBaseUrl { get; set; } = string.Empty;

    /// <summary>Posição do texto. Com CenterHorizontally=true, TextX é ignorado (centraliza na largura).</summary>
    public int TextX { get; set; }
    public int TextY { get; set; } = 400;
    public bool CenterHorizontally { get; set; } = true;

    public float FontSize { get; set; } = 64;
    public string FontColorHex { get; set; } = "#FFFFFF";

    /// <summary>TTF usado para desenhar o nome. Default: DejaVu Sans Bold (presente no Ubuntu).</summary>
    public string FontPath { get; set; } = "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf";
}
