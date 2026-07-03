using HospitalAccess.Gateway.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace HospitalAccess.Tests.Imaging;

public class FaceImageConverterTests
{
    private static byte[] Encode(int width, int height, IImageEncoder encoder)
    {
        using var image = new Image<Rgba32>(width, height, Color.CadetBlue);
        using var ms = new MemoryStream();
        image.Save(ms, encoder);
        return ms.ToArray();
    }

    [Fact]
    public void Jpeg_dentro_dos_limites_e_devolvido_intacto()
    {
        var jpeg = Encode(480, 640, new JpegEncoder { Quality = 80 });

        var result = FaceImageConverter.ConvertImage(jpeg, 480, 640, 122880);

        Assert.Same(jpeg, result);
    }

    [Fact]
    public void Png_dentro_dos_limites_e_reencodado_para_jpeg()
    {
        // Regressão L9: antes, um PNG dentro dos limites passava direto e o device recebia
        // formato não suportado. Agora deve ser reencodado para JPEG.
        var png = Encode(200, 200, new PngEncoder());

        var result = FaceImageConverter.ConvertImage(png, 480, 640, 122880);

        Assert.NotSame(png, result);
        using var ms = new MemoryStream(result);
        var format = Image.DetectFormat(ms);
        Assert.IsType<JpegFormat>(format);
    }

    [Fact]
    public void Imagem_maior_que_o_limite_e_redimensionada_para_caber()
    {
        var big = Encode(1920, 2560, new JpegEncoder { Quality = 100 });

        var result = FaceImageConverter.ConvertImage(big, 480, 640, 122880);

        using var image = Image.Load<Rgba32>(result);
        Assert.True(image.Width <= 480);
        Assert.True(image.Height <= 640);
        Assert.True(result.Length <= 122880);
    }
}
