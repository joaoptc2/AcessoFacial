using System.Buffers.Binary;
using HospitalAccess.Gateway.Imaging;
using SkiaSharp;

namespace HospitalAccess.Tests.Imaging;

/// <summary>Fábrica de imagens para os testes — inclusive as malformadas de propósito.</summary>
internal static class TestImages
{
    /// <summary>Imagem de cor sólida codificada no formato pedido (JPEG, PNG ou WebP).</summary>
    public static byte[] Solid(int width, int height, SKColor color,
        SKEncodedImageFormat format = SKEncodedImageFormat.Jpeg, int quality = 90)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        bitmap.Erase(color);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, quality);
        return data.ToArray();
    }

    public static void SaveSolid(string path, int width, int height, SKColor color) =>
        File.WriteAllBytes(path, Solid(width, height, color));

    public static ImageHeader Header(byte[] bytes) =>
        ImageDecoding.Identify(bytes) ?? throw new InvalidOperationException("não é uma imagem aceita");

    public static ImageHeader Header(string path) => Header(File.ReadAllBytes(path));

    public static SKBitmap Decode(byte[] bytes) => ImageDecoding.Decode(bytes, out _);

    /// <summary>
    /// GIF 1x1 válido. Formato que o decodificador RECONHECE mas que não está na lista aceita:
    /// prova que o filtro é por formato, e não só "o Skia conseguiu abrir".
    /// </summary>
    public static readonly byte[] Gif1x1 = Convert.FromBase64String("R0lGODlhAQABAIAAAP///wAAACH5BAEAAAAALAAAAAABAAEAAAICRAEAOw==");

    /// <summary>Cabeçalho TIFF clássico (little-endian) — formato que o ImageSharp 2.1 aceitava.</summary>
    public static readonly byte[] TiffHeader = { 0x49, 0x49, 0x2A, 0x00, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00 };

    /// <summary>
    /// BigTIFF de 24 bytes com contagem de entradas de 5 bilhões — o padrão do alerta
    /// GHSA-wmxv-xphr-5c9g, que prendia a thread do ImageSharp 2.1 por mais de 5 s.
    /// </summary>
    public static byte[] BigTiffEntryCountLoop()
    {
        var bytes = new byte[24];
        bytes[0] = 0x49; bytes[1] = 0x49;                       // "II" little-endian
        bytes[2] = 0x2B;                                        // 43 = BigTIFF
        bytes[4] = 8;                                           // tamanho do offset
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(8), 16);           // 1º IFD
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(16), 5_000_000_000); // nº de entradas
        return bytes;
    }

    /// <summary>
    /// PNG de poucos bytes que DECLARA <paramref name="width"/> × <paramref name="height"/> no
    /// cabeçalho (com CRC correto) e não traz pixel nenhum — a "bomba" de memória: quem confia
    /// no cabeçalho e aloca antes de olhar, pede gigabytes.
    /// </summary>
    public static byte[] PngDeclaring(int width, int height)
    {
        using var ms = new MemoryStream();
        ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0), width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8;  // bits por canal
        ihdr[9] = 2;  // RGB
        WriteChunk(ms, "IHDR", ihdr);
        // IDAT mínimo (fluxo zlib vazio): sem ele o decodificador nem aceita o cabeçalho.
        WriteChunk(ms, "IDAT", new byte[] { 0x78, 0x9C, 0x03, 0x00, 0x00, 0x00, 0x00, 0x01 });
        WriteChunk(ms, "IEND", Array.Empty<byte>());
        return ms.ToArray();
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);
        var typeAndData = System.Text.Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        stream.Write(typeAndData);
        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(typeAndData));
        stream.Write(crc);
    }

    private static uint Crc32(byte[] bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in bytes)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return ~crc;
    }
}
