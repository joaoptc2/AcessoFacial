using System.Text;

namespace HospitalAccess.Application.Qr;

/// <summary>
/// Gera o payload do QR Code de abertura de porta (Appendix 8 do protocolo 8190H).
///
/// Estrutura ANTES da criptografia (14 bytes):
///   [0..8]  (9 bytes) número do cartão/credencial
///   [9..12] (4 bytes) validade em formato de tempo comprimido:
///           segundo 6b, minuto 6b, hora 5b, dia 5b, mês 4b, ano 6b
///           (ano armazenado = ano real - 2018)
///   [13]    (1 byte)  CRC8 sobre os 13 bytes anteriores (calculado ANTES da RC4)
///
/// O conteúdo final do QR são esses 14 bytes criptografados com RC4.
/// Chave RC4 = ASCII da string RC4_KEY_STRING.
///
/// Nota do protocolo: o leitor transmite 15 bytes (0x2E + 13 dados + 1 checksum).
/// Aqui geramos o token que a pessoa apresenta; a validação é feita pelo controlador.
/// </summary>
public sealed class QrAccessTokenService
{
    /// <summary>Ano base do campo de ano no tempo comprimido (protocolo: 2018).</summary>
    public const int YearBase = 2018;

    /// <summary>Chave RC4: usar o ASCII desta string (não o valor hex).</summary>
    public const string Rc4KeyString = "1e30b3ec0f634874956e27627dfc2c46";

    /// <summary>
    /// Constrói os 14 bytes do QR (já criptografados), prontos para virar imagem QR.
    /// </summary>
    /// <param name="cardNumber">9 bytes do número do cartão/credencial do visitante.</param>
    /// <param name="expiration">Data/hora de expiração do acesso.</param>
    public byte[] BuildEncryptedToken(ReadOnlySpan<byte> cardNumber, DateTime expiration)
    {
        if (cardNumber.Length != 9)
            throw new ArgumentException("Card number must be exactly 9 bytes.", nameof(cardNumber));

        Span<byte> plain = stackalloc byte[14];

        // [0..8] cartão
        cardNumber.CopyTo(plain);

        // [9..12] tempo comprimido
        PackCompressedTime(expiration, plain.Slice(9, 4));

        // [13] CRC8 sobre os 13 primeiros bytes
        plain[13] = Crc8(plain.Slice(0, 13));

        // RC4 sobre os 14 bytes
        var key = Encoding.ASCII.GetBytes(Rc4KeyString);
        var output = new byte[14];
        Rc4.Transform(key, plain, output);
        return output;
    }

    /// <summary>
    /// Empacota data/hora no formato comprimido de 4 bytes (32 bits) do protocolo:
    /// segundo(6) minuto(6) hora(5) dia(5) mês(4) ano(6). Ano = ano real - 2018.
    ///
    /// ⚠️ A ORDEM/ENDIANNESS dos bits precisa ser confirmada contra um QR real gerado
    /// pelo fabricante. O layout de campos está correto pelo documento; a montagem
    /// em big-endian abaixo é a interpretação mais natural (campos do mais significativo
    /// para o menos significativo). Valide com um token de referência antes de produção.
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
        packed = (packed << 6) | (uint)(dt.Second & 0x3F); // 6 bits
        packed = (packed << 6) | (uint)(dt.Minute & 0x3F); // 6 bits
        packed = (packed << 5) | (uint)(dt.Hour & 0x1F);   // 5 bits
        packed = (packed << 5) | (uint)(dt.Day & 0x1F);    // 5 bits
        packed = (packed << 4) | (uint)(dt.Month & 0x0F);  // 4 bits
        packed = (packed << 6) | (uint)(year & 0x3F);      // 6 bits
        // total = 32 bits

        dest4[0] = (byte)(packed >> 24);
        dest4[1] = (byte)(packed >> 16);
        dest4[2] = (byte)(packed >> 8);
        dest4[3] = (byte)packed;
    }

    /// <summary>
    /// CRC8 sobre os dados antes da criptografia.
    /// ⚠️ Polinômio/inicialização NÃO estão especificados no trecho do documento.
    /// Este é o CRC-8 clássico (poly 0x07, init 0x00). CONFIRME contra o SDK/fabricante
    /// — pode ser outra variante (ex.: soma simples, poly 0x31, init 0xFF).
    /// </summary>
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
