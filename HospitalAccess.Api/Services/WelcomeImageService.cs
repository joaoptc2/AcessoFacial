using System.Text;
using HospitalAccess.Gateway.Imaging;
using SkiaSharp;

namespace HospitalAccess.Api.Services;

/// <summary>
/// Gera a tela de boas-vindas do leito: carrega a IMAGEM BASE fornecida pelo hospital, desenha
/// o nome do paciente na posição pré-definida e salva como JPG (nome estável
/// <c>leito-{controllerId}.jpg</c>) no diretório público servido em <c>/welcome/*</c> — o
/// Home Assistant recebe a URL e exibe na TV do quarto. A configuração (imagem base, posição,
/// fonte, cor, URL pública) é lida POR CHAMADA do <see cref="RuntimeSettingsProvider"/> —
/// salvar na tela de Configurações vale imediatamente, sem restart.
///
/// <para>
/// Desenho com SkiaSharp (MIT) desde nov/2026; antes, ImageSharp.Drawing 1.0 sobre o ImageSharp
/// 2.1, trocado por alertas de segurança sem correção na linha 2.x. A posição vertical do texto
/// reproduz a do ImageSharp (<see cref="BaselineOffset"/>) para que o X/Y já configurado sobre a
/// arte do hospital continue caindo no mesmo lugar.
/// </para>
/// </summary>
public sealed class WelcomeImageService
{
    // Limite DOCUMENTADO do Google Cast para exibição de imagens: 1280x720. Acima disso o
    // receptor BAIXA o arquivo normalmente e falha em silêncio na renderização (a TV fica no
    // splash do HA) — visto em produção com a base 1920x1080 e confirmado por tcpdump (200 OK,
    // download completo, nada na tela). A arte final é reduzida para caber no limite; como o
    // texto já foi desenhado, a composição inteira escala proporcionalmente.
    internal const int CastMaxWidth = 1280;
    internal const int CastMaxHeight = 720;

    private readonly RuntimeSettingsProvider _settings;
    private readonly ILogger<WelcomeImageService> _logger;
    private readonly object _fontLock = new();
    // Cache sensível a mudanças: a fonte agora pode ser TROCADA em runtime (upload pela tela),
    // inclusive sobrescrevendo o mesmo caminho — o cache invalida por caminho E data do arquivo.
    // A fonte anterior não é descartada na troca: uma geração em andamento pode estar usando-a.
    private SKTypeface? _typeface;
    private string? _cachedFontPath;
    private DateTime _cachedFontWriteUtc;

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
        Directory.CreateDirectory(cfg.OutputDirectory);
        var fileName = FileNameFor(controllerId);
        var outputPath = Path.Combine(cfg.OutputDirectory, fileName);

        using var image = await RenderAsync(cfg, patientName, ct);

        // Sempre JPG (requisito): grava num temporário e move por cima — o HA nunca busca um
        // arquivo pela metade.
        var tempPath = outputPath + ".tmp";
        await File.WriteAllBytesAsync(tempPath, ImageDecoding.EncodeJpeg(image, 90), ct);
        File.Move(tempPath, outputPath, overwrite: true);

        _logger.LogInformation("Tela de boas-vindas gerada para o leito {ControllerId}: {Path} (\"{Name}\").",
            controllerId, outputPath, patientName);

        return PublicUrlFor(cfg.PublicBaseUrl, fileName);
    }

    /// <summary>
    /// Prévia da tela com um nome de exemplo: mesmos parâmetros efetivos da geração real, mas
    /// devolve os BYTES do JPEG sem escrever nada no diretório público.
    /// </summary>
    public async Task<byte[]> RenderPreviewAsync(string patientName, CancellationToken ct = default)
    {
        using var image = await RenderAsync(_settings.Welcome, patientName, ct);
        return ImageDecoding.EncodeJpeg(image, 90);
    }

    /// <summary>
    /// Recebe o upload da imagem base: valida decodificando, re-encoda como JPEG (q95) e salva
    /// FORA do diretório público (no pai do WelcomeOutputDirectory — ex.:
    /// /var/lib/hospitalaccess/welcome-base.jpg). Devolve caminho e dimensões.
    /// </summary>
    public async Task<(string Path, int Width, int Height)> SaveBaseImageAsync(Stream content, CancellationToken ct = default)
    {
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct);

        SKBitmap image;
        try
        {
            image = ImageDecoding.Decode(buffer.ToArray(), out _);
        }
        catch (ImageDecodeException ex)
        {
            throw new InvalidOperationException($"O arquivo enviado não é uma imagem válida — {ex.Message}.", ex);
        }

        using (image)
        {
            var cfg = _settings.Welcome;
            var parentDir = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(cfg.OutputDirectory)));
            var basePath = Path.Combine(string.IsNullOrEmpty(parentDir) ? cfg.OutputDirectory : parentDir, "welcome-base.jpg");
            Directory.CreateDirectory(Path.GetDirectoryName(basePath)!);

            // Achatada sobre branco: a base de um PNG transparente não pode virar fundo preto.
            using var flat = ImageDecoding.DrawOnWhite(image, image.Width, image.Height,
                SKRect.Create(0, 0, image.Width, image.Height));
            var tempPath = basePath + ".tmp";
            await File.WriteAllBytesAsync(tempPath, ImageDecoding.EncodeJpeg(flat, 95), ct);
            File.Move(tempPath, basePath, overwrite: true);

            _logger.LogInformation("Imagem base de boas-vindas atualizada: {Path} ({W}x{H}).", basePath, image.Width, image.Height);
            return (basePath, image.Width, image.Height);
        }
    }

    /// <summary>Núcleo compartilhado: carrega a base e desenha o nome (validações com mensagens claras).</summary>
    private async Task<SKBitmap> RenderAsync(RuntimeSettingsProvider.WelcomeSettings cfg, string patientName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cfg.BaseImagePath))
            throw new InvalidOperationException(
                "Imagem base da tela de boas-vindas não configurada — envie/defina na tela de Configurações (ou BedManagement:WelcomeBaseImagePath).");
        if (!File.Exists(cfg.BaseImagePath))
            throw new InvalidOperationException(
                $"Imagem base da tela de boas-vindas não encontrada: {cfg.BaseImagePath}");

        SKBitmap image;
        try
        {
            image = ImageDecoding.Decode(await File.ReadAllBytesAsync(cfg.BaseImagePath, ct), out _);
        }
        catch (ImageDecodeException ex)
        {
            throw new InvalidOperationException(
                $"A imagem base da tela de boas-vindas ({cfg.BaseImagePath}) não pôde ser aberta: {ex.Message}.", ex);
        }

        try
        {
            var typeface = GetTypeface(cfg.FontPath);
            var color = ParseHexColor(string.IsNullOrWhiteSpace(cfg.FontColorHex) ? "#FFFFFF" : cfg.FontColorHex);
            // NFC: sem um motor de shaping, um acento DECOMPOSTO (letra + acento combinante, como
            // alguns teclados e cópias de PDF produzem) sairia deslocado. Composto, é um glifo só.
            var text = patientName.Normalize(NormalizationForm.FormC);

            using var font = new SKFont(typeface, cfg.FontSize) { Edging = SKFontEdging.Antialias, Subpixel = true };
            using var paint = new SKPaint { Color = color, IsAntialias = true };
            var textWidth = font.MeasureText(text, paint);
            var x = cfg.CenterHorizontally
                ? Math.Max(0, (image.Width - textWidth) / 2f)
                : cfg.TextX;
            var baseline = cfg.TextY + BaselineOffset(typeface, cfg.FontSize);

            using (var canvas = new SKCanvas(image))
            {
                canvas.DrawText(text, x, baseline, SKTextAlign.Left, font, paint);
                canvas.Flush();
            }

            if (image.Width > CastMaxWidth || image.Height > CastMaxHeight)
            {
                var scale = Math.Min((double)CastMaxWidth / image.Width, (double)CastMaxHeight / image.Height);
                var width = Math.Max(1, (int)Math.Round(image.Width * scale));
                var height = Math.Max(1, (int)Math.Round(image.Height * scale));
                var reduced = ImageDecoding.DrawOnWhite(image, width, height, SKRect.Create(0, 0, width, height));
                image.Dispose();
                image = reduced;
            }

            return image;
        }
        catch
        {
            image.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Distância do topo configurado (TextY) até a linha de base do texto, reproduzindo o
    /// layout do SixLabors.Fonts 1.0 usado até nov/2026: a linha tem a altura de 1 em, e a
    /// altura de linha da fonte (ascendente − descendente + espaçamento) é centralizada nela.
    /// As métricas vêm da tabela OS/2 ("typo") quando a fonte marca USE_TYPO_METRICS, e da
    /// hhea nos demais casos — a mesma escolha do ImageSharp.
    ///
    /// Medido contra o ImageSharp a 200 pt: DejaVu Sans Bold 169 px, Liberation Sans Bold 166,
    /// FreeSans Bold 150 — o Skia com a ascendente própria dele desenharia 10 a 17 px mais baixo,
    /// e o nome sairia da posição já ajustada sobre a arte do hospital.
    /// </summary>
    internal static float BaselineOffset(SKTypeface typeface, float fontSize)
    {
        var unitsPerEm = typeface.UnitsPerEm;
        var os2 = TryGetTable(typeface, "OS/2");
        var hhea = TryGetTable(typeface, "hhea");
        if (unitsPerEm > 0 && (hhea is { Length: >= 10 } || os2 is { Length: >= 74 }))
        {
            var useTypo = os2 is { Length: >= 74 } && ((ReadUInt16(os2, 62) & 0x80) != 0 || hhea is not { Length: >= 10 });
            var (ascender, descender, lineGap) = useTypo
                ? (ReadInt16(os2!, 68), ReadInt16(os2!, 70), ReadInt16(os2!, 72))
                : (ReadInt16(hhea!, 4), ReadInt16(hhea!, 6), ReadInt16(hhea!, 8));
            var lineHeight = ascender - descender + lineGap;
            return fontSize * (ascender - (lineHeight - unitsPerEm) / 2f) / unitsPerEm;
        }

        // Fonte sem as tabelas (raro): a ascendente que o próprio Skia calcula.
        using var font = new SKFont(typeface, fontSize);
        font.GetFontMetrics(out var metrics);
        return -metrics.Ascent;
    }

    private static byte[]? TryGetTable(SKTypeface typeface, string tag)
    {
        var code = (uint)((tag[0] << 24) | (tag[1] << 16) | (tag[2] << 8) | tag[3]);
        return typeface.TryGetTableData(code, out var data) ? data : null;
    }

    private static short ReadInt16(byte[] table, int offset) => (short)((table[offset] << 8) | table[offset + 1]);
    private static ushort ReadUInt16(byte[] table, int offset) => (ushort)((table[offset] << 8) | table[offset + 1]);

    /// <summary>
    /// Cor no formato que o ImageSharp aceitava: #RGB, #RGBA, #RRGGBB ou #RRGGBBAA (alfa no FIM —
    /// o parser do Skia lê #AARRGGBB, com o alfa no começo, e trocaria as cores de 8 dígitos).
    /// </summary>
    internal static SKColor ParseHexColor(string hex)
    {
        var digits = hex.Trim().TrimStart('#');
        if (digits.Length is 3 or 4)
            digits = string.Concat(digits.Select(c => $"{c}{c}"));
        if (digits.Length == 6)
            digits += "FF";
        if (digits.Length != 8 || !uint.TryParse(digits, System.Globalization.NumberStyles.HexNumber, null, out var rgba))
            throw new InvalidOperationException(
                $"Cor do texto da tela de boas-vindas inválida: '{hex}'. Use um hex de 6 dígitos (ex.: #FFFFFF).");
        return new SKColor((byte)(rgba >> 24), (byte)(rgba >> 16), (byte)(rgba >> 8), (byte)rgba);
    }

    public static string FileNameFor(Guid controllerId) => $"leito-{controllerId:N}.jpg";

    private static string PublicUrlFor(string publicBaseUrl, string fileName)
    {
        var baseUrl = publicBaseUrl.TrimEnd('/');
        // Cache-bust: o nome é estável por leito; o HA/TV não pode reusar a arte anterior.
        return $"{baseUrl}/welcome/{fileName}?v={DateTime.UtcNow.Ticks}";
    }

    /// <summary>Fontes comuns por distro, sondadas quando o FontPath configurado não existe.</summary>
    public static readonly string[] FontCandidates =
    {
        "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf",
        "/usr/share/fonts/truetype/liberation/LiberationSans-Bold.ttf",
        "/usr/share/fonts/truetype/freefont/FreeSansBold.ttf",
        "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
        "/usr/share/fonts/truetype/liberation/LiberationSans-Regular.ttf",
    };

    /// <summary>
    /// Recebe o upload de uma fonte TTF/OTF: valida carregando com o motor de fontes, salva
    /// FORA do diretório público (welcome-font.ttf, ao lado da imagem base) e devolve o
    /// caminho + o nome da família (para a tela confirmar qual fonte entrou).
    /// </summary>
    public async Task<(string Path, string FamilyName)> SaveFontAsync(Stream content, CancellationToken ct = default)
    {
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct);

        string familyName;
        buffer.Position = 0;
        using (var probe = SKTypeface.FromStream(buffer))
        {
            // Null = o motor de fontes não reconheceu o arquivo; sem família = fonte sem nome
            // legível, que não daria para confirmar na tela.
            if (probe is null || string.IsNullOrWhiteSpace(probe.FamilyName))
                throw new InvalidOperationException("O arquivo enviado não é uma fonte TTF/OTF válida.");
            familyName = probe.FamilyName;
        }

        var cfg = _settings.Welcome;
        var parentDir = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(cfg.OutputDirectory)));
        var fontPath = Path.Combine(string.IsNullOrEmpty(parentDir) ? cfg.OutputDirectory : parentDir, "welcome-font.ttf");
        Directory.CreateDirectory(Path.GetDirectoryName(fontPath)!);

        var tempPath = fontPath + ".tmp";
        await File.WriteAllBytesAsync(tempPath, buffer.ToArray(), ct);
        File.Move(tempPath, fontPath, overwrite: true);

        _logger.LogInformation("Fonte de boas-vindas atualizada: {Path} (família \"{Family}\").", fontPath, familyName);
        return (fontPath, familyName);
    }

    private SKTypeface GetTypeface(string fontPath)
    {
        lock (_fontLock)
        {
            var resolved = ResolveFontPath(fontPath);
            if (resolved is null)
                throw new InvalidOperationException(
                    $"Nenhuma fonte TTF encontrada (configurada: '{fontPath}'; sondadas: {string.Join(", ", FontCandidates)} " +
                    "e /usr/share/fonts). Instale uma fonte (ex.: `sudo apt install fonts-dejavu-core`), envie uma pela " +
                    "tela de Configurações ou aponte BedManagement:FontPath para um .ttf existente.");

            var writeUtc = File.GetLastWriteTimeUtc(resolved);
            if (_typeface is { } cached &&
                string.Equals(_cachedFontPath, resolved, StringComparison.Ordinal) &&
                _cachedFontWriteUtc == writeUtc)
                return cached;

            if (!string.Equals(resolved, fontPath, StringComparison.Ordinal))
                _logger.LogWarning(
                    "Fonte configurada '{Configured}' não existe — usando '{Resolved}' no lugar (instale fonts-dejavu-core, envie uma fonte pela tela ou ajuste BedManagement:FontPath).",
                    fontPath, resolved);

            // Lida para a memória (e não FromFile): o upload pela tela sobrescreve o arquivo no
            // mesmo caminho, e um typeface mapeado no arquivo antigo não deve depender dele.
            var typeface = SKTypeface.FromData(SKData.CreateCopy(File.ReadAllBytes(resolved)))
                ?? throw new InvalidOperationException($"A fonte '{resolved}' não pôde ser carregada (arquivo TTF/OTF inválido).");
            _typeface = typeface;
            _cachedFontPath = resolved;
            _cachedFontWriteUtc = writeUtc;
            return typeface;
        }
    }

    /// <summary>Caminho configurado → candidatos comuns → primeiro .ttf do sistema. Null = nada encontrado.</summary>
    private static string? ResolveFontPath(string configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath)) return configuredPath;
        foreach (var candidate in FontCandidates)
            if (File.Exists(candidate)) return candidate;
        try
        {
            return Directory.Exists("/usr/share/fonts")
                ? Directory.EnumerateFiles("/usr/share/fonts", "*.ttf", SearchOption.AllDirectories).FirstOrDefault()
                : null;
        }
        catch
        {
            // Diretório ilegível (permissões) — trata como "não achou".
            return null;
        }
    }
}
