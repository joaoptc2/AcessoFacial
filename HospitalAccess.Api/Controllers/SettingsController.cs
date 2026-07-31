using HospitalAccess.Api.Services;
using HospitalAccess.Domain.Entities;
using HospitalAccess.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HospitalAccess.Api.Controllers;

// SEMÂNTICA (protege contra clientes antigos que não enviam os campos novos):
// - campo NULL/omitido = MANTER o valor atual (nunca regride nada por omissão);
// - string VAZIA = voltar a HERDAR o appsettings (decisão explícita da tela);
// - segredos: valor novo = trocar; Clear* = apagar (volta a herdar); omitido = manter.
// HomeAssistantEnabled/WelcomeTextY/WelcomeFontSize viajam como STRING pela mesma razão
// ("on"/"off"/"400"… = valor; "" = herdar; null = manter).
public record UpdateSettingsRequest(
    int EventPhotoRetentionDays,
    int AccessLogRetentionDays,
    int AlarmLogRetentionDays,
    int ControllerAuditRetentionDays,
    string QrFormat,
    // Senhas padrão dos aparelhos: comunicação (fábrica FFFFFFFF) e painel web (fábrica 1409).
    string? DeviceDefaultCommunicationPassword = null,
    bool ClearDeviceDefaultCommunicationPassword = false,
    string? DeviceDefaultApiPassword = null,
    bool ClearDeviceDefaultApiPassword = false,
    string? HomeAssistantEnabled = null,       // "on" | "off" | "" (herdar) | null (manter)
    string? HomeAssistantBaseUrl = null,
    string? HomeAssistantToken = null,
    bool ClearHomeAssistantToken = false,
    string? HomeAssistantWelcomeService = null,
    string? HomeAssistantClearService = null,
    string? WelcomeBaseImagePath = null,
    string? WelcomePublicBaseUrl = null,
    string? WelcomeTextY = null,
    string? WelcomeFontSize = null,
    string? WelcomeFontColorHex = null,
    // Caminho da fonte enviada ("" = voltar à fonte do sistema/appsettings; null = manter).
    string? WelcomeFontPath = null);

/// <summary>
/// Configurações globais do sistema (linha única): retenção de dados (LGPD), formato do QR,
/// senha padrão dos aparelhos, integração Home Assistant e tela de boas-vindas — tudo
/// administrável pela tela, sem linha de comando. Campo vazio herda o appsettings
/// (RuntimeSettingsProvider). Segredos nunca são devolvidos pelo GET. Só Admin.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = "Admin")]
public class SettingsController : ControllerBase
{
    private readonly AccessDbContext _db;
    private readonly RuntimeSettingsProvider _runtime;
    private readonly HomeAssistantClient _ha;
    private readonly WelcomeImageService _welcome;

    public SettingsController(AccessDbContext db, RuntimeSettingsProvider runtime, HomeAssistantClient ha,
        WelcomeImageService welcome)
    {
        _db = db;
        _runtime = runtime;
        _ha = ha;
        _welcome = welcome;
    }

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var settings = await GetOrCreateAsync(ct);
        var effective = _runtime.HomeAssistant;
        return Ok(new
        {
            settings.EventPhotoRetentionDays,
            settings.AccessLogRetentionDays,
            settings.AlarmLogRetentionDays,
            settings.ControllerAuditRetentionDays,
            settings.QrFormat,
            // Segredos: só o indicador de presença (o valor nunca sai do servidor).
            HasDeviceDefaultCommunicationPassword = !string.IsNullOrEmpty(settings.DeviceDefaultCommunicationPassword),
            HasDeviceDefaultApiPassword = !string.IsNullOrEmpty(settings.DeviceDefaultApiPassword),
            settings.HomeAssistantEnabled,
            settings.HomeAssistantBaseUrl,
            HasHomeAssistantToken = !string.IsNullOrEmpty(settings.HomeAssistantToken),
            settings.HomeAssistantWelcomeService,
            settings.HomeAssistantClearService,
            settings.WelcomeBaseImagePath,
            settings.WelcomePublicBaseUrl,
            settings.WelcomeTextY,
            settings.WelcomeFontSize,
            settings.WelcomeFontColorHex,
            settings.WelcomeFontPath,
            // Efetivo (banco-ou-appsettings), para a tela mostrar o que vale de fato.
            HomeAssistantEffective = new
            {
                effective.Enabled,
                effective.BaseUrl,
                HasToken = !string.IsNullOrEmpty(effective.Token),
                effective.WelcomeService,
                effective.ClearService,
            },
            settings.UpdatedAtUtc,
            settings.UpdatedByUsername,
        });
    }

    [HttpPut]
    public async Task<IActionResult> Update([FromBody] UpdateSettingsRequest request, CancellationToken ct)
    {
        if (request.EventPhotoRetentionDays < 0 || request.AccessLogRetentionDays < 0
            || request.AlarmLogRetentionDays < 0 || request.ControllerAuditRetentionDays < 0)
            return BadRequest("Os prazos de retenção não podem ser negativos (0 = reter indefinidamente).");
        if (request.QrFormat is not ("Appendix8Rc4" or "PlainText"))
            return BadRequest("QrFormat deve ser 'Appendix8Rc4' ou 'PlainText'.");
        if (request.HomeAssistantEnabled is not (null or "" or "on" or "off"))
            return BadRequest("HomeAssistantEnabled deve ser 'on', 'off' ou vazio (herdar).");

        int? textY = null;
        if (!string.IsNullOrWhiteSpace(request.WelcomeTextY))
        {
            if (!int.TryParse(request.WelcomeTextY, out var y) || y < 0)
                return BadRequest("A posição Y do texto de boas-vindas deve ser um inteiro não-negativo.");
            textY = y;
        }
        float? fontSize = null;
        if (!string.IsNullOrWhiteSpace(request.WelcomeFontSize))
        {
            if (!float.TryParse(request.WelcomeFontSize, System.Globalization.CultureInfo.InvariantCulture, out var fs) || fs <= 0)
                return BadRequest("O tamanho da fonte de boas-vindas deve ser maior que zero.");
            fontSize = fs;
        }
        var color = request.WelcomeFontColorHex?.Trim();
        if (!string.IsNullOrEmpty(color) && !System.Text.RegularExpressions.Regex.IsMatch(color, "^#?[0-9a-fA-F]{6}$"))
            return BadRequest("A cor do texto deve ser um hex de 6 dígitos (ex.: #FFFFFF).");

        var settings = await GetOrCreateAsync(ct);
        settings.EventPhotoRetentionDays = request.EventPhotoRetentionDays;
        settings.AccessLogRetentionDays = request.AccessLogRetentionDays;
        settings.AlarmLogRetentionDays = request.AlarmLogRetentionDays;
        settings.ControllerAuditRetentionDays = request.ControllerAuditRetentionDays;
        settings.QrFormat = request.QrFormat;

        // Segredos: novo valor = trocar; Clear* = apagar (volta a herdar/fábrica); omitido = manter.
        if (!string.IsNullOrWhiteSpace(request.DeviceDefaultCommunicationPassword))
            settings.DeviceDefaultCommunicationPassword = request.DeviceDefaultCommunicationPassword.Trim();
        if (request.ClearDeviceDefaultCommunicationPassword)
            settings.DeviceDefaultCommunicationPassword = string.Empty;
        if (!string.IsNullOrWhiteSpace(request.DeviceDefaultApiPassword))
            settings.DeviceDefaultApiPassword = request.DeviceDefaultApiPassword.Trim();
        if (request.ClearDeviceDefaultApiPassword)
            settings.DeviceDefaultApiPassword = string.Empty;
        if (!string.IsNullOrWhiteSpace(request.HomeAssistantToken))
            settings.HomeAssistantToken = request.HomeAssistantToken.Trim();
        if (request.ClearHomeAssistantToken)
            settings.HomeAssistantToken = string.Empty;

        // Campos de runtime: NULL = manter (cliente antigo não regride nada); "" = herdar.
        if (request.HomeAssistantEnabled is not null)
            settings.HomeAssistantEnabled = request.HomeAssistantEnabled switch
            {
                "on" => true,
                "off" => false,
                _ => null,
            };
        if (request.HomeAssistantBaseUrl is not null)
            settings.HomeAssistantBaseUrl = request.HomeAssistantBaseUrl.Trim();
        if (request.HomeAssistantWelcomeService is not null)
            settings.HomeAssistantWelcomeService = request.HomeAssistantWelcomeService.Trim();
        if (request.HomeAssistantClearService is not null)
            settings.HomeAssistantClearService = request.HomeAssistantClearService.Trim();
        if (request.WelcomeBaseImagePath is not null)
            settings.WelcomeBaseImagePath = request.WelcomeBaseImagePath.Trim();
        if (request.WelcomePublicBaseUrl is not null)
            settings.WelcomePublicBaseUrl = request.WelcomePublicBaseUrl.Trim().TrimEnd('/');
        if (request.WelcomeTextY is not null)
            settings.WelcomeTextY = textY;
        if (request.WelcomeFontSize is not null)
            settings.WelcomeFontSize = fontSize;
        if (color is not null)
            settings.WelcomeFontColorHex = color.Length > 0 && !color.StartsWith('#') ? $"#{color}" : color;
        if (request.WelcomeFontPath is not null)
            settings.WelcomeFontPath = request.WelcomeFontPath.Trim();

        settings.UpdatedAtUtc = DateTime.UtcNow;
        settings.UpdatedByUsername = User.Identity?.Name;

        await _db.SaveChangesAsync(ct);
        // Vale imediatamente para todos os consumidores (HA, boas-vindas, senha padrão).
        _runtime.Invalidate();
        return NoContent();
    }

    /// <summary>Sonda a REST API do HA com a configuração EFETIVA (salve antes de testar).</summary>
    [HttpPost("homeassistant/test")]
    public async Task<IActionResult> TestHomeAssistant(CancellationToken ct)
    {
        var (ok, message) = await _ha.TestAsync(ct);
        return Ok(new { ok, message });
    }

    /// <summary>
    /// Upload da imagem base da tela de boas-vindas: valida, re-encoda como JPEG e salva fora
    /// do diretório público; o caminho passa a valer imediatamente (WelcomeBaseImagePath).
    /// </summary>
    [HttpPost("welcome-image")]
    [RequestSizeLimit(10 * 1024 * 1024)]
    public async Task<IActionResult> UploadWelcomeImage(IFormFile? file, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return BadRequest("Envie um arquivo de imagem (JPG ou PNG).");

        string path;
        int width, height;
        try
        {
            await using var stream = file.OpenReadStream();
            (path, width, height) = await _welcome.SaveBaseImageAsync(stream, ct);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }

        var settings = await GetOrCreateAsync(ct);
        settings.WelcomeBaseImagePath = path;
        settings.UpdatedAtUtc = DateTime.UtcNow;
        settings.UpdatedByUsername = User.Identity?.Name;
        await _db.SaveChangesAsync(ct);
        _runtime.Invalidate();

        return Ok(new { path, width, height });
    }

    /// <summary>
    /// Upload da fonte (TTF/OTF) usada para desenhar o nome do paciente: valida com o motor de
    /// fontes, salva fora do diretório público e passa a valer imediatamente (WelcomeFontPath).
    /// </summary>
    [HttpPost("welcome-font")]
    [RequestSizeLimit(5 * 1024 * 1024)]
    public async Task<IActionResult> UploadWelcomeFont(IFormFile? file, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return BadRequest("Envie um arquivo de fonte (.ttf ou .otf).");

        string path, familyName;
        try
        {
            await using var stream = file.OpenReadStream();
            (path, familyName) = await _welcome.SaveFontAsync(stream, ct);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }

        var settings = await GetOrCreateAsync(ct);
        settings.WelcomeFontPath = path;
        settings.UpdatedAtUtc = DateTime.UtcNow;
        settings.UpdatedByUsername = User.Identity?.Name;
        await _db.SaveChangesAsync(ct);
        _runtime.Invalidate();

        return Ok(new { path, familyName });
    }

    /// <summary>Prévia da tela de boas-vindas com um nome de exemplo (usa a configuração EFETIVA — salve antes).</summary>
    [HttpGet("welcome-image/preview")]
    public async Task<IActionResult> PreviewWelcomeImage([FromQuery] string? name, CancellationToken ct)
    {
        var sample = string.IsNullOrWhiteSpace(name) ? "Maria da Silva" : name.Trim();
        try
        {
            var bytes = await _welcome.RenderPreviewAsync(sample, ct);
            return File(bytes, "image/jpeg");
        }
        catch (InvalidOperationException ex)
        {
            // Base/fonte ausente → mensagem acionável em vez de 500.
            return BadRequest(ex.Message);
        }
    }

    private async Task<SystemSettings> GetOrCreateAsync(CancellationToken ct)
    {
        // Por chave (linha única): FirstOrDefault sem filtro dispara o warning de EF
        // "First without OrderBy" a cada chamada — ruído no log espelhado do modo dev.
        var settings = await _db.SystemSettings
            .FirstOrDefaultAsync(s => s.Id == SystemSettings.SingletonId, ct);
        if (settings is null)
        {
            settings = new SystemSettings { Id = SystemSettings.SingletonId };
            _db.SystemSettings.Add(settings);
            await _db.SaveChangesAsync(ct);
        }
        return settings;
    }
}
