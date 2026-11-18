using HospitalAccess.Gateway.Imaging;
using SkiaSharp;
using Xunit;

namespace HospitalAccess.Tests.Imaging;

public class FaceImageConverterTests
{
    private static readonly SKColor CadetBlue = new(95, 158, 160);

    [Fact]
    public void Jpeg_dentro_dos_limites_e_devolvido_intacto()
    {
        var jpeg = TestImages.Solid(480, 640, CadetBlue, quality: 80);

        var result = FaceImageConverter.ConvertImage(jpeg, 480, 640, 122880);

        Assert.Same(jpeg, result);
    }

    [Fact]
    public void Png_dentro_dos_limites_e_reencodado_para_jpeg()
    {
        // Regressão L9: antes, um PNG dentro dos limites passava direto e o device recebia
        // formato não suportado. Agora deve ser reencodado para JPEG.
        var png = TestImages.Solid(200, 200, CadetBlue, SKEncodedImageFormat.Png);

        var result = FaceImageConverter.ConvertImage(png, 480, 640, 122880);

        Assert.NotSame(png, result);
        Assert.Equal(SKEncodedImageFormat.Jpeg, TestImages.Header(result).Format);
    }

    [Fact]
    public void Imagem_maior_que_o_limite_e_redimensionada_para_caber()
    {
        var big = TestImages.Solid(1920, 2560, CadetBlue, quality: 100);

        var result = FaceImageConverter.ConvertImage(big, 480, 640, 122880);

        var header = TestImages.Header(result);
        Assert.Equal(SKEncodedImageFormat.Jpeg, header.Format);
        Assert.True(header.Width <= 480);
        Assert.True(header.Height <= 640);
        Assert.True(result.Length <= 122880);
    }

    /// <summary>
    /// Paisagem vai para o canvas retrato 480x640 centralizada, com faixas BRANCAS (não pretas)
    /// em cima e embaixo — o mesmo que o helper do fabricante faz.
    /// </summary>
    [Fact]
    public void Paisagem_e_centralizada_em_canvas_branco()
    {
        var paisagem = TestImages.Solid(1600, 900, SKColors.Black, quality: 95);

        var result = FaceImageConverter.ConvertImage(paisagem, 480, 640, 122880);

        using var bitmap = TestImages.Decode(result);
        Assert.Equal(480, bitmap.Width);
        Assert.Equal(640, bitmap.Height);
        Assert.True(bitmap.GetPixel(240, 10).Red > 240, "faixa de cima deveria ser branca");
        Assert.True(bitmap.GetPixel(240, 320).Red < 20, "o centro deveria ser a foto (preta)");
    }

    /// <summary>
    /// JPEG não tem alfa: um PNG transparente codificado direto sairia com fundo PRETO. O
    /// conversor achata sobre branco — o fundo que o leitor espera.
    /// </summary>
    [Fact]
    public void Png_transparente_vira_fundo_branco()
    {
        var transparente = TestImages.Solid(200, 200, SKColors.Transparent, SKEncodedImageFormat.Png);

        var result = FaceImageConverter.ConvertImage(transparente, 480, 640, 122880);

        using var bitmap = TestImages.Decode(result);
        var pixel = bitmap.GetPixel(100, 100);
        Assert.True(pixel.Red > 240 && pixel.Green > 240 && pixel.Blue > 240, $"fundo saiu {pixel}");
    }

    /// <summary>Formato fora da lista não é decodificado — nem chega ao aparelho.</summary>
    [Fact]
    public void Formato_fora_da_lista_e_recusado()
    {
        var ex = Assert.Throws<ImageDecodeException>(() => FaceImageConverter.ConvertImage(TestImages.Gif1x1, 480, 640, 122880));
        Assert.Contains("GIF", ex.Message);
    }
}
