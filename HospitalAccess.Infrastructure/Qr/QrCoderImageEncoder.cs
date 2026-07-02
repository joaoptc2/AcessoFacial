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
}
