using HospitalAccess.Application.Qr;
using QRCoder;

namespace HospitalAccess.Infrastructure.Qr;

/// <summary>
/// Renderiza o texto do token (Base64 — ver QrAccessTokenService) como PNG usando QRCoder.
/// PngByteQRCode desenha o PNG manualmente (sem System.Drawing), multiplataforma.
/// </summary>
public sealed class QrCoderImageEncoder : IQrImageEncoder
{
    private const int PixelsPerModule = 10;

    public byte[] EncodePng(string token)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(token, QRCodeGenerator.ECCLevel.M);
        var pngQrCode = new PngByteQRCode(data);
        return pngQrCode.GetGraphic(PixelsPerModule);
    }

    /// <summary>
    /// Modo byte puro (CreateQrCode(byte[], ECCLevel)) — para o token binário do Apêndice 8.
    /// NÃO converter para Base64/Hex: o leitor decodifica os bytes crus do QR e os decripta.
    /// </summary>
    public byte[] EncodePng(byte[] tokenBytes)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(tokenBytes, QRCodeGenerator.ECCLevel.M);
        var pngQrCode = new PngByteQRCode(data);
        return pngQrCode.GetGraphic(PixelsPerModule);
    }
}
