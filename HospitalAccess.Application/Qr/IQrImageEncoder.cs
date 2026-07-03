namespace HospitalAccess.Application.Qr;

/// <summary>
/// Converte o token do QR em imagem PNG para entregar ao visitante. Dois modos, conforme o
/// formato do token (ver QrAccessTokenService): texto (Base64) ou bytes crus (Apêndice 8/RC4).
/// Implementado na Infrastructure com QRCoder.
/// </summary>
public interface IQrImageEncoder
{
    /// <summary>Codifica um token de TEXTO (ex.: Base64) como QR PNG.</summary>
    byte[] EncodePng(string token);

    /// <summary>
    /// Codifica um token BINÁRIO (bytes crus, ex.: os 14 bytes cifrados do Apêndice 8) como QR
    /// PNG em modo byte — NÃO converter para Base64/Hex antes: o leitor espera os bytes crus.
    /// </summary>
    byte[] EncodePng(byte[] tokenBytes);
}
