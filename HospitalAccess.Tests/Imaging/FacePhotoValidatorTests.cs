using System.Diagnostics;
using System.Text;
using HospitalAccess.Gateway.Imaging;
using SkiaSharp;
using Xunit;

namespace HospitalAccess.Tests.Imaging;

/// <summary>
/// Validação da foto NO UPLOAD. O objetivo é que o erro apareça na tela de cadastro, não como
/// uma linha na lista de falhas de sincronização minutos depois.
/// </summary>
public class FacePhotoValidatorTests
{
    private static readonly SKColor CadetBlue = new(95, 158, 160);

    private static byte[] Jpeg(int width, int height) => TestImages.Solid(width, height, CadetBlue, quality: 80);

    [Fact]
    public void Aceita_JpegNoTamanhoDoAparelho()
    {
        var jpeg = Jpeg(480, 640);

        Assert.True(FacePhotoValidator.TryValidate(jpeg, out var erro));
        Assert.Null(erro);
    }

    [Fact]
    public void Aceita_PngGrande_QueSeraConvertido()
    {
        // O conversor reencoda para JPEG; a validação não pode recusar formato que o sistema aceita.
        var png = TestImages.Solid(1000, 1400, CadetBlue, SKEncodedImageFormat.Png);

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
        var icone = Jpeg(32, 32);

        Assert.False(FacePhotoValidator.TryValidate(icone, out var erro));
        Assert.Contains("32x32", erro);
        Assert.Contains($"{FacePhotoValidator.MinWidth}x{FacePhotoValidator.MinHeight}", erro);
    }

    [Fact]
    public void Aceita_ExatamenteNoPisoDeResolucao()
    {
        var noPiso = Jpeg(FacePhotoValidator.MinWidth, FacePhotoValidator.MinHeight);

        Assert.True(FacePhotoValidator.TryValidate(noPiso, out var erro));
        Assert.Null(erro);
    }

    /// <summary>
    /// Upload interrompido. O ImageSharp 2.x completava o JPEG truncado e a foto seguia até o
    /// aparelho, que respondia "sem rosto" — este teste fixava isso como limite conhecido. Com o
    /// SkiaSharp, a recusa vem aqui, na tela, dizendo o que fazer.
    /// </summary>
    [Fact]
    public void JpegTruncado_EhRecusado()
    {
        var jpeg = Jpeg(480, 640);
        var truncado = jpeg[..(jpeg.Length / 3)];

        Assert.False(FacePhotoValidator.TryValidate(truncado, out var erro));
        Assert.Contains("incompleto", erro);
    }

    /// <summary>
    /// Formatos que o ImageSharp 2.1 aceitava e o sistema nunca pediu (TIFF, GIF, BMP...) são
    /// recusados pelo cabeçalho. Quatro dos cinco alertas que motivaram a troca eram de TIFF.
    /// </summary>
    [Fact]
    public void FormatosForaDaLista_SaoRecusados()
    {
        Assert.False(FacePhotoValidator.TryValidate(TestImages.Gif1x1, out var erroGif));
        Assert.Contains("JPG ou PNG", erroGif);
        Assert.False(FacePhotoValidator.TryValidate(TestImages.TiffHeader, out var erroTiff));
        Assert.Contains("JPG ou PNG", erroTiff);
    }

    /// <summary>
    /// O caso do alerta GHSA-wmxv-xphr-5c9g: 24 bytes que prendiam a thread do decodificador por
    /// mais de 5 s. Agora não é nem decodificado.
    /// </summary>
    [Fact]
    public void BigTiffDoAlerta_EhRecusadoNaHora()
    {
        var relogio = Stopwatch.StartNew();

        Assert.False(FacePhotoValidator.TryValidate(TestImages.BigTiffEntryCountLoop(), out var erro));
        Assert.True(relogio.Elapsed < TimeSpan.FromSeconds(1), $"levou {relogio.Elapsed.TotalSeconds:F1} s");
        Assert.Contains("não é uma imagem", erro);
    }

    /// <summary>
    /// "Bomba" de memória: um PNG de 50 bytes que declara 30.000 × 30.000 pixels (3,6 GB se
    /// alocado). O teto é conferido no cabeçalho, antes de alocar.
    /// </summary>
    [Fact]
    public void ImagemQueDeclaraDimensoesGigantes_EhRecusadaSemAlocar()
    {
        var bomba = TestImages.PngDeclaring(30_000, 30_000);
        var antes = GC.GetTotalAllocatedBytes(precise: true);

        Assert.False(FacePhotoValidator.TryValidate(bomba, out var erro));
        Assert.Contains("30000x30000", erro);
        Assert.Contains("megapixels", erro);
        Assert.True(GC.GetTotalAllocatedBytes(precise: true) - antes < 10_000_000, "alocou memória demais para recusar");
    }
}
