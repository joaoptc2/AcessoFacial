using System.Text;
using HospitalAccess.Gateway.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace HospitalAccess.Tests.Imaging;

/// <summary>
/// Validação da foto NO UPLOAD. O objetivo é que o erro apareça na tela de cadastro, não como
/// uma linha na lista de falhas de sincronização minutos depois.
/// </summary>
public class FacePhotoValidatorTests
{
    private static byte[] Encode(int width, int height, IImageEncoder encoder)
    {
        using var image = new Image<Rgba32>(width, height, Color.CadetBlue);
        using var ms = new MemoryStream();
        image.Save(ms, encoder);
        return ms.ToArray();
    }

    [Fact]
    public void Aceita_JpegNoTamanhoDoAparelho()
    {
        var jpeg = Encode(480, 640, new JpegEncoder { Quality = 80 });

        Assert.True(FacePhotoValidator.TryValidate(jpeg, out var erro));
        Assert.Null(erro);
    }

    [Fact]
    public void Aceita_PngGrande_QueSeraConvertido()
    {
        // O conversor reencoda para JPEG; a validação não pode recusar formato que o sistema aceita.
        var png = Encode(1000, 1400, new PngEncoder());

        Assert.True(FacePhotoValidator.TryValidate(png, out var erro));
        Assert.Null(erro);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(new byte[0])]
    public void Recusa_ConteudoVazio(byte[]? bytes)
    {
        Assert.False(FacePhotoValidator.TryValidate(bytes, out var erro));
        Assert.Contains("vazia", erro);
    }

    [Fact]
    public void Recusa_ArquivoQueNaoEhImagem()
    {
        // O caso real: PDF ou documento renomeado para .jpg. Antes disso ser validado aqui, o
        // arquivo era gravado no banco e só estourava na conversão, dentro da sincronização.
        var naoImagem = Encoding.UTF8.GetBytes("%PDF-1.4 isto definitivamente nao e uma imagem");

        Assert.False(FacePhotoValidator.TryValidate(naoImagem, out var erro));
        Assert.Contains("não é uma imagem", erro);
    }

    [Fact]
    public void Recusa_ImagemPequenaDemais_ComAsMedidasNaMensagem()
    {
        // Um ícone nunca produz feature code: o aparelho sempre responderia "sem rosto".
        var icone = Encode(32, 32, new JpegEncoder { Quality = 80 });

        Assert.False(FacePhotoValidator.TryValidate(icone, out var erro));
        Assert.Contains("32x32", erro);
        Assert.Contains($"{FacePhotoValidator.MinWidth}x{FacePhotoValidator.MinHeight}", erro);
    }

    [Fact]
    public void Aceita_ExatamenteNoPisoDeResolucao()
    {
        var noPiso = Encode(FacePhotoValidator.MinWidth, FacePhotoValidator.MinHeight, new JpegEncoder { Quality = 80 });

        Assert.True(FacePhotoValidator.TryValidate(noPiso, out var erro));
        Assert.Null(erro);
    }

    /// <summary>
    /// LIMITE CONHECIDO, fixado de propósito: o ImageSharp 2.x é tolerante com JPEG truncado —
    /// decodifica o que veio e completa o resto em vez de lançar. Um upload interrompido, então,
    /// PASSA nesta validação e só é recusado pelo aparelho depois (como "sem rosto", que hoje
    /// vira quarentena com mensagem clara — ver FaceUploadCode.NoFaceInPhoto).
    ///
    /// O teste existe para que isso seja uma decisão registrada e não uma surpresa: se um dia o
    /// ImageSharp passar a ser estrito, este teste quebra e avisa que a validação ficou mais forte.
    /// </summary>
    [Fact]
    public void JpegTruncado_Passa_LimiteConhecidoDoDecodificador()
    {
        var jpeg = Encode(480, 640, new JpegEncoder { Quality = 80 });
        var truncado = jpeg[..(jpeg.Length / 3)];

        Assert.True(FacePhotoValidator.TryValidate(truncado, out var erro));
        Assert.Null(erro);
    }
}
