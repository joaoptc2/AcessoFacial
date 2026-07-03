using System.Text;

namespace HospitalAccess.Application.Qr;

/// <summary>
/// Gera o payload do QR Code de abertura de porta. Suporta DOIS formatos, selecionáveis nas
/// Configurações do sistema (<see cref="Domain.Entities.SystemSettings.QrFormat"/>):
///
/// 1. <b>PlainText</b> — Base64 de "user_id={UserCode}_time={microssegundos Unix}". Foi deduzido
///    de UMA amostra de QR do fabricante; em campo, alguns firmwares REJEITAM esse formato
///    ("QR inválido"). Não tem criptografia/checksum.
///
/// 2. <b>Appendix8Rc4</b> — o formato binário DOCUMENTADO no protocolo 8190H (Apêndice 8):
///    14 bytes = cartão(9) + validade em tempo comprimido(4) + CRC8(1), cifrados com RC4. É o
///    formato que o firmware valida offline (decripta, confere CRC8 e a validade). Use este se o
///    aparelho recusa o texto simples.
///
/// ⚠️ No Apêndice 8, o polinômio do CRC8 e a ordem dos bits do tempo comprimido NÃO estão
/// totalmente especificados no documento — a implementação abaixo usa a interpretação mais
/// natural. Se ainda der "QR inválido", é preciso confirmar contra um QR real e funcional gerado
/// pelo software oficial do fabricante para ESTE modelo (ver README / seção de QR).
/// </summary>
public sealed class QrAccessTokenService
{
    /// <summary>Ano base do campo de ano no tempo comprimido (protocolo: 2018).</summary>
    public const int YearBase = 2018;

    /// <summary>Chave RC4: usar o ASCII desta string (não o valor hex).</summary>
    public const string Rc4KeyString = "1e30b3ec0f634874956e27627dfc2c46";

    // ---------------------------------------------------------------------------------------
    // Formato 1 — texto simples (Base64)
    // ---------------------------------------------------------------------------------------

    /// <summary>Monta o texto do QR: Base64 de "user_id={userCode}_time={microssegundos desde a época Unix}".</summary>
    public string BuildAccessToken(uint userCode, DateTime timestampUtc)
    {
        var microsecondsSinceEpoch = (long)(timestampUtc.ToUniversalTime() - DateTime.UnixEpoch).TotalMicroseconds;
        var plainText = $"user_id={userCode}_time={microsecondsSinceEpoch}";
        return Convert.ToBase64String(Encoding.ASCII.GetBytes(plainText));
    }

    // ---------------------------------------------------------------------------------------
    // Formato 2 — binário Apêndice 8 (RC4 + CRC8 + tempo comprimido)
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Constrói os 14 bytes do QR (já cifrados com RC4), prontos para virar imagem QR em modo byte.
    /// </summary>
    /// <param name="cardNumber">9 bytes do número do cartão/credencial (ver VisitorCardNumber).</param>
    /// <param name="expiration">Data/hora de expiração do acesso (validade do QR).</param>
    public byte[] BuildAppendix8Token(ReadOnlySpan<byte> cardNumber, DateTime expiration)
    {
        if (cardNumber.Length != 9)
            throw new ArgumentException("Card number must be exactly 9 bytes.", nameof(cardNumber));

        Span<byte> plain = stackalloc byte[14];
        cardNumber.CopyTo(plain);                       // [0..8] cartão
        PackCompressedTime(expiration, plain.Slice(9, 4)); // [9..12] tempo comprimido
        plain[13] = Crc8(plain.Slice(0, 13));           // [13] CRC8 sobre os 13 primeiros

        var key = Encoding.ASCII.GetBytes(Rc4KeyString);
        var output = new byte[14];
        Rc4.Transform(key, plain, output);
        return output;
    }

    /// <summary>Atalho: gera o token Apêndice 8 para um visitante (cartão = UserCode).</summary>
    public byte[] BuildAppendix8TokenForUser(uint userCode, DateTime expirationUtc) =>
        BuildAppendix8Token(VisitorCardNumber.FromUserCode(userCode), expirationUtc.ToLocalTime());

    /// <summary>
    /// Empacota data/hora no formato comprimido de 4 bytes (32 bits): segundo(6) minuto(6) hora(5)
    /// dia(5) mês(4) ano(6). Ano = ano real − 2018.
    /// </summary>
    internal static void PackCompressedTime(DateTime dt, Span<byte> dest4)
    {
        if (dest4.Length != 4)
            throw new ArgumentException("Destination must be 4 bytes.", nameof(dest4));

        int year = dt.Year - YearBase; // 6 bits (0-63)
        if (year is < 0 or > 63)
            throw new ArgumentOutOfRangeException(nameof(dt),
                $"Year out of range for base {YearBase} (allowed {YearBase}..{YearBase + 63}).");

        uint packed = 0;
        packed = (packed << 6) | (uint)(dt.Second & 0x3F);
        packed = (packed << 6) | (uint)(dt.Minute & 0x3F);
        packed = (packed << 5) | (uint)(dt.Hour & 0x1F);
        packed = (packed << 5) | (uint)(dt.Day & 0x1F);
        packed = (packed << 4) | (uint)(dt.Month & 0x0F);
        packed = (packed << 6) | (uint)(year & 0x3F);

        dest4[0] = (byte)(packed >> 24);
        dest4[1] = (byte)(packed >> 16);
        dest4[2] = (byte)(packed >> 8);
        dest4[3] = (byte)packed;
    }

    /// <summary>CRC8 clássico (poly 0x07, init 0x00) sobre os dados antes da criptografia.</summary>
    internal static byte Crc8(ReadOnlySpan<byte> data)
    {
        const byte poly = 0x07;
        byte crc = 0x00;
        foreach (var b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
                crc = (byte)((crc & 0x80) != 0 ? (crc << 1) ^ poly : crc << 1);
        }
        return crc;
    }
}
