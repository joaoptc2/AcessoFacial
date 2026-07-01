using HospitalAccess.Application.Qr;
using QRCoder;

namespace HospitalAccess.Infrastructure.Qr;

/// <summary>
/// Renderiza o token binário (14 bytes já cifrados com RC4) como PNG usando QRCoder em
/// modo byte puro (QRCodeGenerator.CreateQrCode(byte[], ECCLevel)) — NÃO converter para
/// Base64/Hex antes: o leitor do controlador espera os bytes crus decodificados do QR,
/// e uma representação textual mudaria completamente o payload que ele decripta.
/// PngByteQRCode desenha o PNG manualmente (sem System.Drawing), multiplataforma.
/// </summary>
public sealed class QrCoderImageEncoder : IQrImageEncoder
{
    private const int PixelsPerModule = 10;

    public byte[] EncodePng(byte[] tokenBytes)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(tokenBytes, QRCodeGenerator.ECCLevel.M);
        var pngQrCode = new PngByteQRCode(data);
        return pngQrCode.GetGraphic(PixelsPerModule);
    }
}
