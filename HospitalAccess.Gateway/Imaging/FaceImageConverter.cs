using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace HospitalAccess.Gateway.Imaging;

/// <summary>
/// Porta multiplataforma do ImageTool.ConvertImage do projeto de exemplo
/// (DoNetDrive.Protocol.Fingerprint.Test/Model/ImageTool.cs).
///
/// O helper original do fabricante usa System.Drawing (GDI+), que é Windows-only
/// desde o .NET 7 e lança PlatformNotSupportedException em Linux — inviável para uma
/// API ASP.NET Core on-premise que pode rodar em qualquer SO. Reimplementado aqui com
/// ImageSharp (SixLabors.ImageSharp, fixado em 2.x/Apache-2.0 — evite subir para 3.x
/// sem revisar a licença comercial "Six Labors Split License").
///
/// Mantém a mesma semântica do original: se a imagem já está dentro dos limites,
/// devolve os bytes originais sem reprocessar; senão redimensiona preservando o
/// aspect ratio, centraliza em um canvas branco 480x640 e reduz a qualidade JPEG
/// iterativamente até caber em iMaxSize bytes.
/// </summary>
public static class FaceImageConverter
{
    public static byte[] ConvertImage(byte[] sourceJpg, int maxWidth, int maxHeight, int maxSizeBytes)
    {
        using var image = Image.Load<Rgba32>(sourceJpg, out IImageFormat format);

        // O controlador só aceita JPEG. Só devolvemos os bytes originais sem reprocessar se a
        // imagem JÁ é JPEG e está dentro dos limites; um PNG/WebP dentro dos limites precisa ser
        // reencodado para JPEG (antes passava direto e o device recebia formato não suportado).
        var isJpeg = format is JpegFormat;
        if (isJpeg && image.Width <= maxWidth && image.Height <= maxHeight && sourceJpg.Length <= maxSizeBytes)
            return sourceJpg;

        // Se está dentro das dimensões mas não é JPEG (ou passou do tamanho), reencoda como JPEG
        // sem redimensionar desnecessariamente.
        if (image.Width <= maxWidth && image.Height <= maxHeight)
        {
            for (var quality = 100; quality > 0; quality -= 2)
            {
                using var jpegMs = new MemoryStream();
                image.Save(jpegMs, new JpegEncoder { Quality = quality });
                if (jpegMs.Length <= maxSizeBytes)
                    return jpegMs.ToArray();
            }
        }

        float rateW = (float)maxWidth / image.Width;
        float rateH = (float)maxHeight / image.Height;
        float rate = Math.Min(rateW, rateH);

        int scaledWidth = Math.Max(1, (int)(image.Width * rate));
        int scaledHeight = Math.Max(1, (int)(image.Height * rate));

        using var canvas = new Image<Rgba32>(maxWidth, maxHeight, Color.White);
        image.Mutate(ctx => ctx.Resize(scaledWidth, scaledHeight));

        var offsetX = (maxWidth - scaledWidth) / 2;
        var offsetY = (maxHeight - scaledHeight) / 2;
        canvas.Mutate(ctx => ctx.DrawImage(image, new Point(offsetX, offsetY), 1f));

        for (var quality = 100; quality > 0; quality -= 2)
        {
            using var ms = new MemoryStream();
            canvas.Save(ms, new JpegEncoder { Quality = quality });
            if (ms.Length <= maxSizeBytes)
                return ms.ToArray();
        }

        throw new InvalidOperationException(
            $"Não foi possível comprimir a foto para <= {maxSizeBytes} bytes mesmo na qualidade JPEG mínima.");
    }
}
