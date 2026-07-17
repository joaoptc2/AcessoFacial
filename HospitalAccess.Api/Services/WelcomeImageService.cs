using HospitalAccess.Api.Options;
using Microsoft.Extensions.Options;
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
/// Home Assistant recebe a URL e exibe na TV do quarto. Tudo configurável em
/// <see cref="BedManagementOptions"/> (posição, fonte, cor, diretórios).
/// </summary>
public sealed class WelcomeImageService
{
    private readonly BedManagementOptions _options;
    private readonly ILogger<WelcomeImageService> _logger;
    private readonly object _fontLock = new();
    private FontFamily? _fontFamily;

    public WelcomeImageService(IOptions<BedManagementOptions> options, ILogger<WelcomeImageService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public bool Configured =>
        !string.IsNullOrWhiteSpace(_options.WelcomeBaseImagePath) && File.Exists(_options.WelcomeBaseImagePath);

    /// <summary>
    /// Gera (sobrescrevendo) o JPG do leito e devolve a URL pública para o HA — com
    /// cache-bust, já que o nome do arquivo é estável por leito.
    /// </summary>
    public async Task<string> GenerateAsync(Guid controllerId, string patientName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_options.WelcomeBaseImagePath))
            throw new InvalidOperationException(
                "BedManagement:WelcomeBaseImagePath não configurado — aponte para a imagem base da tela de boas-vindas.");
        if (!File.Exists(_options.WelcomeBaseImagePath))
            throw new InvalidOperationException(
                $"Imagem base da tela de boas-vindas não encontrada: {_options.WelcomeBaseImagePath}");

        Directory.CreateDirectory(_options.WelcomeOutputDirectory);
        var fileName = FileNameFor(controllerId);
        var outputPath = Path.Combine(_options.WelcomeOutputDirectory, fileName);

        using var image = await Image.LoadAsync(_options.WelcomeBaseImagePath, ct);

        var font = GetFontFamily().CreateFont(_options.FontSize, FontStyle.Bold);
        var color = Color.ParseHex(_options.FontColorHex);

        var textOptions = new TextOptions(font);
        var size = TextMeasurer.MeasureSize(patientName, textOptions);
        var x = _options.CenterHorizontally
            ? Math.Max(0, (image.Width - size.Width) / 2f)
            : _options.TextX;

        image.Mutate(ctx => ctx.DrawText(patientName, font, color, new PointF(x, _options.TextY)));

        // Sempre JPG (requisito): grava num temporário e move por cima — o HA nunca busca um
        // arquivo pela metade.
        var tempPath = outputPath + ".tmp";
        await image.SaveAsync(tempPath, new JpegEncoder { Quality = 90 }, ct);
        File.Move(tempPath, outputPath, overwrite: true);

        _logger.LogInformation("Tela de boas-vindas gerada para o leito {ControllerId}: {Path} (\"{Name}\").",
            controllerId, outputPath, patientName);

        return PublicUrlFor(fileName);
    }

    public static string FileNameFor(Guid controllerId) => $"leito-{controllerId:N}.jpg";

    private string PublicUrlFor(string fileName)
    {
        var baseUrl = _options.PublicBaseUrl.TrimEnd('/');
        // Cache-bust: o nome é estável por leito; o HA/TV não pode reusar a arte anterior.
        return $"{baseUrl}/welcome/{fileName}?v={DateTime.UtcNow.Ticks}";
    }

    private FontFamily GetFontFamily()
    {
        if (_fontFamily is { } cached) return cached;
        lock (_fontLock)
        {
            if (_fontFamily is { } cached2) return cached2;
            if (string.IsNullOrWhiteSpace(_options.FontPath) || !File.Exists(_options.FontPath))
                throw new InvalidOperationException(
                    $"Fonte TTF não encontrada em '{_options.FontPath}' — configure BedManagement:FontPath " +
                    "(ex.: /usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf).");
            var collection = new FontCollection();
            var family = collection.Add(_options.FontPath);
            _fontFamily = family;
            return family;
        }
    }
}
