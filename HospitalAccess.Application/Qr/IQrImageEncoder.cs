namespace HospitalAccess.Application.Qr;

/// <summary>
/// Converte os bytes do token em uma imagem QR (PNG/SVG) para entregar ao visitante.
/// Implementar na Infrastructure com uma lib (ex.: QRCoder), pois o payload é binário.
/// </summary>
public interface IQrImageEncoder
{
    byte[] EncodePng(byte[] tokenBytes);
}
