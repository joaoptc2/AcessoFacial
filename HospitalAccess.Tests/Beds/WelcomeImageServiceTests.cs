using HospitalAccess.Api.Options;
using HospitalAccess.Api.Services;
using HospitalAccess.Infrastructure.Devices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
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

    private string CreateBaseImage(int width = 800, int height = 600)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "base.jpg");
        using var image = new Image<Rgba32>(width, height, Color.DarkSlateBlue);
        image.Save(path, new JpegEncoder { Quality = 90 });
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

        using var generated = Image.Load(outputPath, out IImageFormat format);
        Assert.Equal("JPEG", format.Name);
        Assert.Equal(800, generated.Width);
        Assert.Equal(600, generated.Height);

        Assert.StartsWith($"http://servidor.local/welcome/leito-{controllerId:N}.jpg?v=", url);
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

        using var generated = Image.Load(files[0], out IImageFormat format);
        Assert.Equal("JPEG", format.Name);
    }

    [Fact]
    public async Task Preview_gera_jpeg_valido_sem_escrever_no_diretorio_publico()
    {
        CreateBaseImage(640, 480);
        var service = CreateService();

        var bytes = await service.RenderPreviewAsync("Nome de Exemplo");

        using var ms = new MemoryStream(bytes);
        using var generated = Image.Load(ms, out IImageFormat format);
        Assert.Equal("JPEG", format.Name);
        Assert.Equal(640, generated.Width);
        Assert.Equal(480, generated.Height);
        // A prévia não pode poluir o diretório público servido em /welcome/*.
        Assert.False(Directory.Exists(Path.Combine(_root, "out")));
    }

    [Fact]
    public async Task Upload_da_base_reencoda_jpeg_fora_do_diretorio_publico()
    {
        var service = CreateService();

        byte[] png;
        using (var source = new Image<Rgba32>(300, 200, Color.DarkGreen))
        using (var ms = new MemoryStream())
        {
            source.Save(ms, new SixLabors.ImageSharp.Formats.Png.PngEncoder());
            png = ms.ToArray();
        }

        using var upload = new MemoryStream(png);
        var (path, width, height) = await service.SaveBaseImageAsync(upload);

        Assert.Equal(300, width);
        Assert.Equal(200, height);
        // Salva no PAI do diretório público (não em /welcome/*).
        Assert.Equal(Path.Combine(_root, "welcome-base.jpg"), path);
        using var saved = Image.Load(path, out IImageFormat format);
        Assert.Equal("JPEG", format.Name);
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
