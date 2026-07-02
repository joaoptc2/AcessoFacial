namespace HospitalAccess.Application.Qr;

/// <summary>
/// Converte o texto do token (Base64 — ver QrAccessTokenService) em uma imagem QR (PNG)
/// para entregar ao visitante. Implementado na Infrastructure com QRCoder.
/// </summary>
public interface IQrImageEncoder
{
    byte[] EncodePng(string token);
}
