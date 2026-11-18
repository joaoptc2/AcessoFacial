using SkiaSharp;

namespace HospitalAccess.Gateway.Imaging;

/// <summary>
/// Porta multiplataforma do ImageTool.ConvertImage do projeto de exemplo
/// (DoNetDrive.Protocol.Fingerprint.Test/Model/ImageTool.cs).
///
/// O helper original do fabricante usa System.Drawing (GDI+), que é Windows-only desde o .NET 7
/// e lança PlatformNotSupportedException em Linux. Reimplementado com SkiaSharp (MIT) — antes
/// era ImageSharp 2.1, trocado em nov/2026 por alertas de segurança sem correção na linha 2.x
/// (a 3.x/4.x muda para a licença comercial "Six Labors Split License").
///
/// Mantém a semântica do original: se a imagem já é JPEG e está dentro dos limites, devolve os
/// bytes originais sem reprocessar; senão redimensiona preservando o aspect ratio, centraliza num
/// canvas branco 480x640 e reduz a qualidade JPEG iterativamente até caber em maxSizeBytes.
/// </summary>
public static class FaceImageConverter
{
    /// <exception cref="ImageDecodeException">Arquivo vazio, fora dos formatos aceitos, grande demais, incompleto ou corrompido.</exception>
    public static byte[] ConvertImage(byte[] sourceJpg, int maxWidth, int maxHeight, int maxSizeBytes)
    {
        // Decodifica SEMPRE, mesmo quando os bytes originais vão ser devolvidos: é o que garante
        // que um JPEG corrompido ou truncado não chega ao aparelho.
        using var image = ImageDecoding.Decode(sourceJpg, out var format);

        // O controlador só aceita JPEG. Só devolvemos os bytes originais sem reprocessar se a
        // imagem JÁ é JPEG e está dentro dos limites; um PNG/WebP dentro dos limites precisa ser
        // reencodado para JPEG (antes passava direto e o device recebia formato não suportado).
        var isJpeg = format == SKEncodedImageFormat.Jpeg;
        if (isJpeg && image.Width <= maxWidth && image.Height <= maxHeight && sourceJpg.Length <= maxSizeBytes)
            return sourceJpg;

        // Dentro das dimensões mas não é JPEG (ou passou do tamanho): reencoda sem redimensionar.
        if (image.Width <= maxWidth && image.Height <= maxHeight)
        {
            using var flat = ImageDecoding.DrawOnWhite(image, image.Width, image.Height,
                SKRect.Create(0, 0, image.Width, image.Height));
            if (EncodeUnder(flat, maxSizeBytes) is { } reencoded)
                return reencoded;
        }

        float rate = Math.Min((float)maxWidth / image.Width, (float)maxHeight / image.Height);
        int scaledWidth = Math.Max(1, (int)(image.Width * rate));
        int scaledHeight = Math.Max(1, (int)(image.Height * rate));
        var offsetX = (maxWidth - scaledWidth) / 2;
        var offsetY = (maxHeight - scaledHeight) / 2;

        using var canvas = ImageDecoding.DrawOnWhite(image, maxWidth, maxHeight,
            SKRect.Create(offsetX, offsetY, scaledWidth, scaledHeight));

        return EncodeUnder(canvas, maxSizeBytes)
            ?? throw new InvalidOperationException(
                $"Não foi possível comprimir a foto para <= {maxSizeBytes} bytes mesmo na qualidade JPEG mínima.");
    }

    /// <summary>Maior qualidade JPEG (de 100 para baixo, de 2 em 2) que cabe no limite; null se nenhuma cabe.</summary>
    private static byte[]? EncodeUnder(SKBitmap bitmap, int maxSizeBytes)
    {
        for (var quality = 100; quality > 0; quality -= 2)
        {
            var jpeg = ImageDecoding.EncodeJpeg(bitmap, quality);
            if (jpeg.Length <= maxSizeBytes)
                return jpeg;
        }
        return null;
    }
}
