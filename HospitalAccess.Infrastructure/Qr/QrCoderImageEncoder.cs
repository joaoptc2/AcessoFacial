using HospitalAccess.Application.Qr;
using QRCoder;

namespace HospitalAccess.Infrastructure.Qr;

/// <summary>
/// Renderiza o texto do token (Base64 — ver QrAccessTokenService) como PNG usando QRCoder.
/// PngByteQRCode desenha o PNG manualmente (sem System.Drawing), multiplataforma.
/// </summary>
public sealed class QrCoderImageEncoder : IQrImageEncoder
{
    // Módulos grandes: um QR físico maior é lido com muito mais folga pela câmera do leitor,
    // e a "zona de silêncio" (borda branca obrigatória) fica em ~PixelsPerModule*4 px, difícil
    // de sumir num recorte acidental. QRs pequenos (10 px/módulo) com borda fina são a causa
    // clássica de "QR inválido"/não-leitura, mesmo com o conteúdo correto.
    private const int PixelsPerModule = 16;

    public byte[] EncodePng(string token)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(token, QRCodeGenerator.ECCLevel.M);
        var pngQrCode = new PngByteQRCode(data);
        // GetGraphic(pixelsPerModule) já desenha a borda branca de 4 módulos exigida pela norma
        // (zona de silêncio). Essa moldura NÃO pode ser recortada, senão o leitor não localiza o QR.
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
