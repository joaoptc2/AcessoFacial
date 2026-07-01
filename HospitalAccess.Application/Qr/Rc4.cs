namespace HospitalAccess.Application.Qr;

/// <summary>
/// RC4 stream cipher. Usado no QR de acesso (Appendix 8).
/// RC4 é simétrico: a mesma função cifra e decifra.
/// (RC4 é criptograficamente fraco; aqui é usado por exigência do protocolo do hardware.)
/// </summary>
internal static class Rc4
{
    public static void Transform(ReadOnlySpan<byte> key, ReadOnlySpan<byte> input, Span<byte> output)
    {
        if (output.Length < input.Length)
            throw new ArgumentException("Output buffer too small.", nameof(output));

        Span<byte> s = stackalloc byte[256];
        for (int i = 0; i < 256; i++) s[i] = (byte)i;

        int j = 0;
        for (int i = 0; i < 256; i++)
        {
            j = (j + s[i] + key[i % key.Length]) & 0xFF;
            (s[i], s[j]) = (s[j], s[i]);
        }

        int x = 0, y = 0;
        for (int k = 0; k < input.Length; k++)
        {
            x = (x + 1) & 0xFF;
            y = (y + s[x]) & 0xFF;
            (s[x], s[y]) = (s[y], s[x]);
            byte ks = s[(s[x] + s[y]) & 0xFF];
            output[k] = (byte)(input[k] ^ ks);
        }
    }
}
