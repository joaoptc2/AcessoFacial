using System.Text;
using HospitalAccess.Application.Qr;
using Xunit;

namespace HospitalAccess.Tests.Qr;

public class QrAccessTokenServiceTests
{
    private readonly QrAccessTokenService _svc = new();

    [Fact]
    public void BuildEncryptedToken_ReturnsExactly14Bytes()
    {
        var card = new byte[9];
        var token = _svc.BuildEncryptedToken(card, new DateTime(2025, 6, 1, 12, 0, 0));
        Assert.Equal(14, token.Length);
    }

    [Fact]
    public void BuildEncryptedToken_RejectsWrongCardLength()
    {
        var badCard = new byte[8];
        Assert.Throws<ArgumentException>(() =>
            _svc.BuildEncryptedToken(badCard, DateTime.Now));
    }

    [Fact]
    public void Rc4_IsSymmetric_RoundTrips()
    {
        var key = Encoding.ASCII.GetBytes(QrAccessTokenService.Rc4KeyString);
        var plain = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14 };

        var cipher = new byte[14];
        Rc4Bridge.Transform(key, plain, cipher);
        var back = new byte[14];
        Rc4Bridge.Transform(key, cipher, back);

        Assert.Equal(plain, back);
    }

    [Fact]
    public void PackCompressedTime_UsesYearBase2018()
    {
        // 2018 -> campo de ano deve ser 0.
        var dest = new byte[4];
        QrAccessTokenService.PackCompressedTime(new DateTime(2018, 1, 1, 0, 0, 0), dest);
        // ano ocupa os 6 bits menos significativos do inteiro empacotado.
        uint packed = (uint)(dest[0] << 24 | dest[1] << 16 | dest[2] << 8 | dest[3]);
        Assert.Equal(0u, packed & 0x3F);
    }

    [Fact]
    public void PackCompressedTime_ThrowsWhenYearOutOfRange()
    {
        var dest = new byte[4];
        // 2018 + 64 = 2082 estoura os 6 bits.
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            QrAccessTokenService.PackCompressedTime(new DateTime(2082, 1, 1), dest));
    }

    [Fact]
    public void Crc8_IsDeterministic()
    {
        var data = new byte[] { 1, 2, 3, 4, 5 };
        var a = QrAccessTokenService.Crc8(data);
        var b = QrAccessTokenService.Crc8(data);
        Assert.Equal(a, b);
    }

    // ⚠️ TESTE DE OURO PENDENTE:
    // Assim que tiver um QR de referência gerado pelo fabricante (cartão + validade
    // conhecidos -> bytes esperados), adicione um teste que compare byte a byte.
    // Isso valida de uma vez o layout de bits do tempo, o polinômio do CRC8 e a RC4.
    [Fact(Skip = "Habilitar quando houver um vetor de referência do fabricante.")]
    public void BuildEncryptedToken_MatchesVendorReferenceVector()
    {
        // var card = ...; var exp = ...; var expected = new byte[]{ ... };
        // Assert.Equal(expected, _svc.BuildEncryptedToken(card, exp));
    }
}

/// <summary>Ponte para testar a RC4 interna sem torná-la pública (via InternalsVisibleTo).</summary>
internal static class Rc4Bridge
{
    public static void Transform(byte[] key, byte[] input, byte[] output)
        => Rc4.Transform(key, input, output);
}
