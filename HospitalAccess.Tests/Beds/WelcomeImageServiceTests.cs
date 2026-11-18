using HospitalAccess.Api.Options;
using HospitalAccess.Api.Services;
using HospitalAccess.Infrastructure.Devices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using HospitalAccess.Tests.Imaging;
using SkiaSharp;
using Xunit;

namespace HospitalAccess.Tests.Beds;

public sealed class WelcomeImageServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hospitalaccess-tests", Guid.NewGuid().ToString("N"));

    // Candidatos por distro; o primeiro (DejaVu Bold) é o default das options.
    private static readonly string[] FontCandidates =
    {
        "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf",
        "/usr/share/fonts/truetype/liberation/LiberationSans-Bold.ttf",
        "/usr/share/fonts/truetype/freefont/FreeSansBold.ttf",
    };

    private static string FindFont() =>
        FontCandidates.FirstOrDefault(File.Exists)
        ?? throw new InvalidOperationException("Nenhuma fonte TTF conhecida encontrada para o teste.");

    private string CreateBaseImage(int width = 800, int height = 600, SKColor? color = null)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "base.jpg");
        TestImages.SaveSolid(path, width, height, color ?? new SKColor(72, 61, 139));
        return path;
    }

    /// <summary>Sem banco nos testes: o RuntimeSettingsProvider cai no fallback do appsettings.</summary>
    private sealed class NoDbScopeFactory : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => throw new InvalidOperationException("sem banco nos testes");
    }

    private WelcomeImageService CreateService(Action<BedManagementOptions>? configure = null)
    {
        var options = new BedManagementOptions
        {
            WelcomeBaseImagePath = Path.Combine(_root, "base.jpg"),
            WelcomeOutputDirectory = Path.Combine(_root, "out"),
            PublicBaseUrl = "http://servidor.local",
            FontPath = FindFont(),
        };
        configure?.Invoke(options);
        var runtime = new RuntimeSettingsProvider(
            new NoDbScopeFactory(),
            Options.Create(new HomeAssistantOptions()),
            Options.Create(options),
            Options.Create(new DeviceHttpOptions()),
            Options.Create(new BackupOptions()),
            NullLogger<RuntimeSettingsProvider>.Instance);
        return new WelcomeImageService(runtime, NullLogger<WelcomeImageService>.Instance);
    }

    [Fact]
    public async Task Gera_jpg_valido_preservando_as_dimensoes_da_base()
    {
        CreateBaseImage(800, 600);
        var service = CreateService();
        var controllerId = Guid.NewGuid();

        var url = await service.GenerateAsync(controllerId, "Maria da Silva");

        var outputPath = Path.Combine(_root, "out", WelcomeImageService.FileNameFor(controllerId));
        Assert.True(File.Exists(outputPath));

        var generated = TestImages.Header(outputPath);
        Assert.Equal(SKEncodedImageFormat.Jpeg, generated.Format);
        Assert.Equal(800, generated.Width);
        Assert.Equal(600, generated.Height);

        Assert.StartsWith($"http://servidor.local/welcome/leito-{controllerId:N}.jpg?v=", url);
    }

    [Fact]
    public async Task Base_full_hd_e_reduzida_para_o_limite_de_imagem_do_cast()
    {
        // O Google Cast baixa mas NÃO exibe imagens acima de 1280x720 (fica no splash) —
        // visto em produção com base 1920x1080. A arte final deve caber no limite.
        CreateBaseImage(1920, 1080);
        var service = CreateService();
        var controllerId = Guid.NewGuid();

        await service.GenerateAsync(controllerId, "Maria da Silva");

        var generated = TestImages.Header(Path.Combine(_root, "out", WelcomeImageService.FileNameFor(controllerId)));
        Assert.Equal(1280, generated.Width);
        Assert.Equal(720, generated.Height);
    }

    [Fact]
    public async Task Preview_tambem_respeita_o_limite_do_cast()
    {
        // A prévia precisa mostrar EXATAMENTE o que a TV vai receber — mesma redução.
        CreateBaseImage(2560, 1440);
        var service = CreateService();

        var bytes = await service.RenderPreviewAsync("Nome de Exemplo");

        var generated = TestImages.Header(bytes);
        Assert.Equal(1280, generated.Width);
        Assert.Equal(720, generated.Height);
    }

    [Fact]
    public async Task Sobrescrita_e_idempotente_e_nao_deixa_temporario()
    {
        CreateBaseImage();
        var service = CreateService();
        var controllerId = Guid.NewGuid();

        await service.GenerateAsync(controllerId, "Primeiro Nome");
        await service.GenerateAsync(controllerId, "Segundo Nome");

        var outDir = Path.Combine(_root, "out");
        // Só o JPG final: o .tmp da escrita atômica foi movido por cima.
        var files = Directory.GetFiles(outDir);
        Assert.Single(files);
        Assert.Equal(WelcomeImageService.FileNameFor(controllerId), Path.GetFileName(files[0]));

        Assert.Equal(SKEncodedImageFormat.Jpeg, TestImages.Header(files[0]).Format);
    }

    [Fact]
    public async Task Preview_gera_jpeg_valido_sem_escrever_no_diretorio_publico()
    {
        CreateBaseImage(640, 480);
        var service = CreateService();

        var bytes = await service.RenderPreviewAsync("Nome de Exemplo");

        var generated = TestImages.Header(bytes);
        Assert.Equal(SKEncodedImageFormat.Jpeg, generated.Format);
        Assert.Equal(640, generated.Width);
        Assert.Equal(480, generated.Height);
        // A prévia não pode poluir o diretório público servido em /welcome/*.
        Assert.False(Directory.Exists(Path.Combine(_root, "out")));
    }

    [Fact]
    public async Task Upload_da_base_reencoda_jpeg_fora_do_diretorio_publico()
    {
        var service = CreateService();

        var png = TestImages.Solid(300, 200, new SKColor(0, 100, 0), SKEncodedImageFormat.Png);

        using var upload = new MemoryStream(png);
        var (path, width, height) = await service.SaveBaseImageAsync(upload);

        Assert.Equal(300, width);
        Assert.Equal(200, height);
        // Salva no PAI do diretório público (não em /welcome/*).
        Assert.Equal(Path.Combine(_root, "welcome-base.jpg"), path);
        Assert.Equal(SKEncodedImageFormat.Jpeg, TestImages.Header(path).Format);
    }

    [Fact]
    public async Task Upload_de_arquivo_invalido_lanca_erro_claro()
    {
        var service = CreateService();
        using var junk = new MemoryStream(new byte[] { 1, 2, 3, 4, 5 });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveBaseImageAsync(junk));
        Assert.Contains("não é uma imagem válida", ex.Message);
    }

    [Fact]
    public async Task Upload_de_fonte_valida_salva_fora_do_publico_e_informa_a_familia()
    {
        CreateBaseImage();
        var service = CreateService();

        await using var ttf = File.OpenRead(FindFont());
        var (path, familyName) = await service.SaveFontAsync(ttf);

        // Ao lado da imagem base (pai do diretório público), nunca em /welcome/*.
        Assert.Equal(Path.Combine(_root, "welcome-font.ttf"), path);
        Assert.False(string.IsNullOrWhiteSpace(familyName));

        // A fonte enviada passa a ser usada na geração (configuração aponta para ela).
        var withUploaded = CreateService(o => o.FontPath = path);
        var controllerId = Guid.NewGuid();
        await withUploaded.GenerateAsync(controllerId, "Maria");
        Assert.True(File.Exists(Path.Combine(_root, "out", WelcomeImageService.FileNameFor(controllerId))));
    }

    [Fact]
    public async Task Upload_de_fonte_invalida_lanca_erro_claro()
    {
        var service = CreateService();
        using var junk = new MemoryStream(new byte[] { 9, 9, 9, 9 });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveFontAsync(junk));
        Assert.Contains("não é uma fonte", ex.Message);
    }

    [Fact]
    public async Task Sem_public_base_url_devolve_caminho_relativo()
    {
        CreateBaseImage();
        var service = CreateService(o => o.PublicBaseUrl = "");
        var controllerId = Guid.NewGuid();

        var url = await service.GenerateAsync(controllerId, "Maria");

        Assert.StartsWith($"/welcome/leito-{controllerId:N}.jpg?v=", url);
    }

    [Fact]
    public async Task Base_ausente_lanca_erro_claro_e_configured_e_false()
    {
        // Diretório existe, mas a imagem base não foi criada.
        Directory.CreateDirectory(_root);
        var service = CreateService();

        Assert.False(service.Configured);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.GenerateAsync(Guid.NewGuid(), "Maria"));
        Assert.Contains("base.jpg", ex.Message);
    }

    [Fact]
    public async Task Caminho_da_base_nao_configurado_lanca_erro_claro()
    {
        var service = CreateService(o => o.WelcomeBaseImagePath = "");

        Assert.False(service.Configured);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.GenerateAsync(Guid.NewGuid(), "Maria"));
        Assert.Contains("WelcomeBaseImagePath", ex.Message);
    }

    [Fact]
    public async Task Fonte_configurada_ausente_cai_no_fallback_de_sistema()
    {
        // Cenário real de produção: servidor sem fonts-dejavu-core no caminho configurado.
        // A sondagem deve achar QUALQUER fonte comum do sistema e a geração seguir normal.
        CreateBaseImage();
        var service = CreateService(o => o.FontPath = Path.Combine(_root, "nao-existe.ttf"));
        var controllerId = Guid.NewGuid();

        var url = await service.GenerateAsync(controllerId, "Maria");

        Assert.True(File.Exists(Path.Combine(_root, "out", WelcomeImageService.FileNameFor(controllerId))));
        Assert.Contains($"/welcome/leito-{controllerId:N}.jpg", url);
    }

    /// <summary>
    /// A troca de biblioteca não pode mover o nome na arte já ajustada pelo hospital. Valores
    /// medidos com o ImageSharp.Drawing 1.0 (a versão substituída) desenhando "HIxH" a 96 pt em
    /// X=100, Y=100: topo do H em 111 px e base em 180 px com a DejaVu Sans Bold.
    /// </summary>
    [SkippableFact]
    public async Task Posicao_do_texto_e_a_mesma_do_ImageSharp()
    {
        const string dejavu = "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf";
        Skip.IfNot(File.Exists(dejavu), "DejaVu Sans Bold não instalada");
        CreateBaseImage(1200, 400, SKColors.Black);
        var service = CreateService(o =>
        {
            o.FontPath = dejavu;
            o.FontSize = 96;
            o.CenterHorizontally = false;
            o.TextX = 100;
            o.TextY = 100;
            o.FontColorHex = "#FFFFFF";
        });

        using var preview = TestImages.Decode(await service.RenderPreviewAsync("HIxH"));

        int top = int.MaxValue, bottom = 0, left = int.MaxValue;
        for (var y = 0; y < preview.Height; y++)
        for (var x = 0; x < preview.Width; x++)
        {
            if (preview.GetPixel(x, y).Red <= 128) continue;
            top = Math.Min(top, y);
            bottom = Math.Max(bottom, y);
            left = Math.Min(left, x);
        }

        Assert.InRange(top, 109, 113);
        Assert.InRange(bottom, 178, 182);
        Assert.InRange(left, 100, 112);
    }

    /// <summary>Linha de base por fonte, contra o medido no ImageSharp a 200 pt.</summary>
    [SkippableTheory]
    [InlineData("/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf", 169)]           // métricas hhea
    [InlineData("/usr/share/fonts/truetype/liberation/LiberationSans-Bold.ttf", 166)]   // hhea com espaçamento
    [InlineData("/usr/share/fonts/truetype/freefont/FreeSansBold.ttf", 150)]            // USE_TYPO_METRICS
    public void Linha_de_base_reproduz_o_layout_do_ImageSharp(string fontPath, float medidoNoImageSharp)
    {
        Skip.IfNot(File.Exists(fontPath), $"{fontPath} não instalada");
        using var typeface = SKTypeface.FromFile(fontPath);

        Assert.InRange(WelcomeImageService.BaselineOffset(typeface, 200), medidoNoImageSharp - 1, medidoNoImageSharp + 1);
    }

    [Theory]
    [InlineData("#FFFFFF", 255, 255, 255, 255)]
    [InlineData("1E90FF", 30, 144, 255, 255)]
    [InlineData("#0F0", 0, 255, 0, 255)]
    [InlineData("#FF000080", 255, 0, 0, 128)] // alfa no FIM, como no ImageSharp (o Skia leria #AARRGGBB)
    public void Cor_aceita_os_formatos_do_ImageSharp(string hex, byte r, byte g, byte b, byte a)
    {
        Assert.Equal(new SKColor(r, g, b, a), WelcomeImageService.ParseHexColor(hex));
    }

    [Fact]
    public void Cor_invalida_lanca_erro_claro()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => WelcomeImageService.ParseHexColor("#XYZ123"));
        Assert.Contains("Cor do texto", ex.Message);
    }

    /// <summary>Base em formato fora da lista (aqui, GIF) é recusada com a mensagem de imagem inválida.</summary>
    [Fact]
    public async Task Upload_da_base_em_formato_fora_da_lista_e_recusado()
    {
        var service = CreateService();
        using var gif = new MemoryStream(TestImages.Gif1x1);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveBaseImageAsync(gif));
        Assert.Contains("não é uma imagem válida", ex.Message);
        Assert.Contains("GIF", ex.Message);
    }

    /// <summary>Nome com acento DECOMPOSTO (letra + acento combinante) é desenhado sem erro.</summary>
    [Fact]
    public async Task Nome_com_acento_decomposto_e_desenhado()
    {
        CreateBaseImage();
        var service = CreateService();

        var bytes = await service.RenderPreviewAsync("Joa\u0303o Conceic\u0327a\u0303o");

        Assert.Equal(SKEncodedImageFormat.Jpeg, TestImages.Header(bytes).Format);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Limpeza best-effort de diretório temporário.
        }
    }
}
