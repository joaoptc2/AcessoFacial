using SkiaSharp;

namespace HospitalAccess.Gateway.Imaging;

/// <summary>Imagem recusada na decodificação. A mensagem é uma frase pronta para a tela.</summary>
public sealed class ImageDecodeException(string message) : Exception(message);

/// <summary>O que dá para saber de uma imagem sem decodificá-la.</summary>
public readonly record struct ImageHeader(SKEncodedImageFormat Format, int Width, int Height);

/// <summary>
/// Porta de entrada ÚNICA de imagens vindas de fora (foto de rosto enviada na recepção, imagem
/// base da tela de boas-vindas). Concentra as três defesas que o decodificador anterior não tinha:
///
/// <list type="bullet">
/// <item><b>Lista curta de formatos</b>: JPEG, PNG e WebP. O ImageSharp 2.1 aceitava também TIFF,
/// BMP, GIF, TGA e PBM, e quatro dos cinco alertas de segurança que levaram à troca (set/2026)
/// eram de TIFF — um deles, um BigTIFF de 24 bytes que prende a thread do decodificador. O
/// sistema nunca pediu nada além de "JPG ou PNG"; aceitar mais só aumentava a superfície.</item>
/// <item><b>Teto de pixels</b> conferido no CABEÇALHO, antes de alocar: um PNG pequeno pode
/// declarar 30.000 × 30.000 pixels e pedir gigabytes de memória ao ser aberto.</item>
/// <item><b>Arquivo incompleto é recusado</b>: o ImageSharp 2.x completava o JPEG truncado e
/// deixava o upload interrompido seguir até o aparelho, que respondia "sem rosto".</item>
/// </list>
/// </summary>
public static class ImageDecoding
{
    /// <summary>Formatos aceitos. Qualquer outro é recusado sem ser decodificado.</summary>
    public static readonly IReadOnlySet<SKEncodedImageFormat> AcceptedFormats = new HashSet<SKEncodedImageFormat>
    {
        SKEncodedImageFormat.Jpeg,
        SKEncodedImageFormat.Png,
        SKEncodedImageFormat.Webp,
    };

    /// <summary>
    /// 50 megapixels: acima de qualquer câmera de celular no modo normal e de uma arte 4K, abaixo
    /// do que derrubaria o servidor (50 MP × 4 bytes = 200 MB por imagem aberta).
    /// </summary>
    public const long MaxPixels = 50_000_000;

    /// <summary>
    /// Formato e dimensões lidos do cabeçalho, sem decodificar os pixels. Null quando o conteúdo
    /// não é uma imagem reconhecida ou está fora de <see cref="AcceptedFormats"/>. Não lança.
    /// </summary>
    public static ImageHeader? Identify(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0) return null;
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data);
        if (codec is null || !AcceptedFormats.Contains(codec.EncodedFormat)) return null;
        return new ImageHeader(codec.EncodedFormat, codec.Info.Width, codec.Info.Height);
    }

    /// <summary>
    /// Decodifica para RGBA. Lança <see cref="ImageDecodeException"/> com a razão em português
    /// quando o conteúdo não é aceito: formato fora da lista, grande demais, incompleto ou
    /// corrompido. O chamador é dono do bitmap devolvido.
    /// </summary>
    public static SKBitmap Decode(byte[]? bytes, out SKEncodedImageFormat format)
    {
        if (bytes is null || bytes.Length == 0)
            throw new ImageDecodeException("o arquivo está vazio");

        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data)
            ?? throw new ImageDecodeException("o arquivo não é uma imagem reconhecida (use JPG ou PNG)");

        format = codec.EncodedFormat;
        if (!AcceptedFormats.Contains(format))
            throw new ImageDecodeException($"o formato {format.ToString().ToUpperInvariant()} não é aceito (use JPG ou PNG)");

        var width = codec.Info.Width;
        var height = codec.Info.Height;
        if (width <= 0 || height <= 0)
            throw new ImageDecodeException("a imagem declara dimensões inválidas");
        if ((long)width * height > MaxPixels)
            throw new ImageDecodeException(
                $"a imagem tem {width}x{height} pixels, acima do limite de {MaxPixels / 1_000_000} megapixels — " +
                "reduza a resolução antes de enviar");

        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        var bitmap = new SKBitmap(info);
        try
        {
            if (bitmap.GetPixels() == IntPtr.Zero)
                throw new ImageDecodeException("não há memória para abrir a imagem");

            var result = codec.GetPixels(info, bitmap.GetPixels());
            return result switch
            {
                SKCodecResult.Success => bitmap,
                SKCodecResult.IncompleteInput => throw new ImageDecodeException(
                    "o arquivo está incompleto — o envio pode ter sido interrompido; envie de novo"),
                _ => throw new ImageDecodeException($"o conteúdo da imagem está corrompido ({result})"),
            };
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    /// <summary>Codifica como JPEG baseline na qualidade pedida (1–100).</summary>
    public static byte[] EncodeJpeg(SKBitmap bitmap, int quality)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, quality)
            ?? throw new InvalidOperationException("O codificador JPEG não produziu saída.");
        return data.ToArray();
    }

    /// <summary>
    /// Amostragem para redimensionar: mipmap linear ao REDUZIR (sem ele, uma foto de 4000 px
    /// levada a 480 px serrilha), cúbica ao ampliar.
    /// </summary>
    public static SKSamplingOptions SamplingFor(int sourceWidth, int targetWidth) =>
        targetWidth < sourceWidth
            ? new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear)
            : new SKSamplingOptions(SKCubicResampler.Mitchell);

    /// <summary>
    /// Desenha <paramref name="source"/> em <paramref name="destination"/> sobre um canvas branco de
    /// <paramref name="canvasWidth"/> × <paramref name="canvasHeight"/>. O branco de fundo também
    /// achata a transparência: JPEG não tem canal alfa, e um PNG transparente codificado direto
    /// sairia com fundo PRETO — péssimo para o reconhecimento facial.
    /// </summary>
    public static SKBitmap DrawOnWhite(SKBitmap source, int canvasWidth, int canvasHeight, SKRect destination)
    {
        var canvasBitmap = new SKBitmap(new SKImageInfo(canvasWidth, canvasHeight, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(canvasBitmap);
        canvas.Clear(SKColors.White);
        using var image = SKImage.FromBitmap(source);
        canvas.DrawImage(image, destination, SamplingFor(source.Width, (int)destination.Width));
        canvas.Flush();
        return canvasBitmap;
    }
}
