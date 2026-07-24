using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace HospitalAccess.Api.Services;

/// <summary>
/// Gera a tela de boas-vindas do leito: carrega a IMAGEM BASE fornecida pelo hospital, desenha
/// o nome do paciente na posição pré-definida e salva como JPG (nome estável
/// <c>leito-{controllerId}.jpg</c>) no diretório público servido em <c>/welcome/*</c> — o
/// Home Assistant recebe a URL e exibe na TV do quarto. A configuração (imagem base, posição,
/// fonte, cor, URL pública) é lida POR CHAMADA do <see cref="RuntimeSettingsProvider"/> —
/// salvar na tela de Configurações vale imediatamente, sem restart.
/// </summary>
public sealed class WelcomeImageService
{
    private readonly RuntimeSettingsProvider _settings;
    private readonly ILogger<WelcomeImageService> _logger;
    private readonly object _fontLock = new();
    private FontFamily? _fontFamily;

    public WelcomeImageService(RuntimeSettingsProvider settings, ILogger<WelcomeImageService> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public bool Configured
    {
        get
        {
            var cfg = _settings.Welcome;
            return !string.IsNullOrWhiteSpace(cfg.BaseImagePath) && File.Exists(cfg.BaseImagePath);
        }
    }

    /// <summary>
    /// Gera (sobrescrevendo) o JPG do leito e devolve a URL pública para o HA — com
    /// cache-bust, já que o nome do arquivo é estável por leito.
    /// </summary>
    public async Task<string> GenerateAsync(Guid controllerId, string patientName, CancellationToken ct = default)
    {
        var cfg = _settings.Welcome;
        if (string.IsNullOrWhiteSpace(cfg.BaseImagePath))
            throw new InvalidOperationException(
                "Imagem base da tela de boas-vindas não configurada — defina na tela de Configurações (ou BedManagement:WelcomeBaseImagePath).");
        if (!File.Exists(cfg.BaseImagePath))
            throw new InvalidOperationException(
                $"Imagem base da tela de boas-vindas não encontrada: {cfg.BaseImagePath}");

        Directory.CreateDirectory(cfg.OutputDirectory);
        var fileName = FileNameFor(controllerId);
        var outputPath = Path.Combine(cfg.OutputDirectory, fileName);

        using var image = await Image.LoadAsync(cfg.BaseImagePath, ct);

        var font = GetFontFamily(cfg.FontPath).CreateFont(cfg.FontSize, FontStyle.Bold);
        var color = Color.ParseHex(string.IsNullOrWhiteSpace(cfg.FontColorHex) ? "#FFFFFF" : cfg.FontColorHex);

        var textOptions = new TextOptions(font);
        var size = TextMeasurer.MeasureSize(patientName, textOptions);
        var x = cfg.CenterHorizontally
            ? Math.Max(0, (image.Width - size.Width) / 2f)
            : cfg.TextX;

        image.Mutate(ctx => ctx.DrawText(patientName, font, color, new PointF(x, cfg.TextY)));

        // Sempre JPG (requisito): grava num temporário e move por cima — o HA nunca busca um
        // arquivo pela metade.
        var tempPath = outputPath + ".tmp";
        await image.SaveAsync(tempPath, new JpegEncoder { Quality = 90 }, ct);
        File.Move(tempPath, outputPath, overwrite: true);

        _logger.LogInformation("Tela de boas-vindas gerada para o leito {ControllerId}: {Path} (\"{Name}\").",
            controllerId, outputPath, patientName);

        return PublicUrlFor(cfg.PublicBaseUrl, fileName);
    }

    public static string FileNameFor(Guid controllerId) => $"leito-{controllerId:N}.jpg";

    private static string PublicUrlFor(string publicBaseUrl, string fileName)
    {
        var baseUrl = publicBaseUrl.TrimEnd('/');
        // Cache-bust: o nome é estável por leito; o HA/TV não pode reusar a arte anterior.
        return $"{baseUrl}/welcome/{fileName}?v={DateTime.UtcNow.Ticks}";
    }

    private FontFamily GetFontFamily(string fontPath)
    {
        // O FontPath vem só do appsettings (não muda em runtime) — cache simples continua válido.
        if (_fontFamily is { } cached) return cached;
        lock (_fontLock)
        {
            if (_fontFamily is { } cached2) return cached2;
            if (string.IsNullOrWhiteSpace(fontPath) || !File.Exists(fontPath))
                throw new InvalidOperationException(
                    $"Fonte TTF não encontrada em '{fontPath}' — configure BedManagement:FontPath " +
                    "(ex.: /usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf).");
            var collection = new FontCollection();
            var family = collection.Add(fontPath);
            _fontFamily = family;
            return family;
        }
    }
}
