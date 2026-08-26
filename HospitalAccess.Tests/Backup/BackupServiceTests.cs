using HospitalAccess.Api.Options;
using HospitalAccess.Api.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace HospitalAccess.Tests.Backup;

/// <summary>
/// A cópia de segurança é baixável pela tela, então o nome do arquivo vem da URL. Estes testes
/// cobrem justamente isso: o parâmetro da rota NÃO pode escapar do diretório de backups. Errar
/// aqui não é um bug de conveniência — é servir arquivo arbitrário do servidor para quem pedir.
/// </summary>
public sealed class BackupServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly BackupService _service;

    public BackupServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"ha-backup-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = "Host=localhost;Database=x;Username=u;Password=p",
                ["DataProtection:KeysPath"] = Path.Combine(_dir, "dpkeys"),
            })
            .Build();

        _service = new BackupService(
            Options.Create(new BackupOptions { Directory = _dir }),
            config,
            NullLogger<BackupService>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* melhor esforço */ }
    }

    private string CreateBackupFile(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, "conteudo-falso");
        return path;
    }

    [Fact]
    public void Resolve_AceitaNomeNoPadrao()
    {
        CreateBackupFile("hospitalaccess-20260826-120000.zip");

        var resolved = _service.ResolveForDownload("hospitalaccess-20260826-120000.zip");

        Assert.NotNull(resolved);
        Assert.StartsWith(Path.GetFullPath(_dir), resolved);
    }

    [Fact]
    public void Resolve_RecusaArquivoQueExisteMasFogeDoPadrao()
    {
        // Existe no diretório, mas o nome não é de uma cópia gerada por nós.
        CreateBackupFile("outro-arquivo.zip");

        Assert.Null(_service.ResolveForDownload("outro-arquivo.zip"));
    }

    [Theory]
    [InlineData("../../../etc/passwd")]
    [InlineData("..%2F..%2Fetc%2Fpasswd")]
    [InlineData("/etc/passwd")]
    [InlineData("hospitalaccess-20260826-120000.zip/../../../etc/passwd")]
    [InlineData("..")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("hospitalaccess-2026-08-26.zip")]
    [InlineData("hospitalaccess-20260826-120000.zip.exe")]
    [InlineData("HOSPITALACCESS-20260826-120000.ZIP")]
    public void Resolve_RecusaTentativaDeEscapar(string nome)
    {
        Assert.Null(_service.ResolveForDownload(nome));
    }

    [Fact]
    public void Resolve_RecusaArquivoInexistenteAindaQueNoPadrao()
    {
        Assert.Null(_service.ResolveForDownload("hospitalaccess-20990101-000000.zip"));
    }

    [Fact]
    public void List_SoDevolveArquivosNoPadrao_MaisRecentePrimeiro()
    {
        CreateBackupFile("hospitalaccess-20260101-000000.zip");
        CreateBackupFile("hospitalaccess-20260826-120000.zip");
        CreateBackupFile("nao-e-backup.zip");
        CreateBackupFile("hospitalaccess-invalido.zip");

        var files = _service.List();

        Assert.Equal(2, files.Count);
        Assert.All(files, f => Assert.StartsWith("hospitalaccess-", f.FileName));
        Assert.DoesNotContain(files, f => f.FileName == "nao-e-backup.zip");
    }

    [Fact]
    public void TryDelete_RecusaCaminhoForaDoDiretorio()
    {
        var fora = Path.Combine(Path.GetTempPath(), $"nao-apagar-{Guid.NewGuid():N}.txt");
        File.WriteAllText(fora, "importante");
        try
        {
            Assert.False(_service.TryDelete($"../{Path.GetFileName(fora)}"));
            Assert.True(File.Exists(fora)); // segue intacto
        }
        finally
        {
            File.Delete(fora);
        }
    }

    [Fact]
    public void Retencao_RemoveAcimaDoTetoDeArquivos()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = "Host=localhost;Database=x;Username=u;Password=p",
        }).Build();
        var service = new BackupService(
            Options.Create(new BackupOptions { Directory = _dir, RetentionDays = 0, MaxFiles = 2 }),
            config, NullLogger<BackupService>.Instance);

        // Datas de criação distintas para a ordenação ser determinística.
        foreach (var (name, minutes) in new[]
                 {
                     ("hospitalaccess-20260826-100000.zip", -30),
                     ("hospitalaccess-20260826-110000.zip", -20),
                     ("hospitalaccess-20260826-120000.zip", -10),
                     ("hospitalaccess-20260826-130000.zip", 0),
                 })
        {
            var path = CreateBackupFile(name);
            File.SetCreationTimeUtc(path, DateTime.UtcNow.AddMinutes(minutes));
        }

        var removed = service.ApplyRetention();

        Assert.Equal(2, removed);
        var remaining = service.List();
        Assert.Equal(2, remaining.Count);
        // Sobram as MAIS RECENTES.
        Assert.Contains(remaining, f => f.FileName == "hospitalaccess-20260826-130000.zip");
        Assert.Contains(remaining, f => f.FileName == "hospitalaccess-20260826-120000.zip");
    }

    [Fact]
    public void Retencao_RemovePorIdade()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = "Host=localhost;Database=x;Username=u;Password=p",
        }).Build();
        var service = new BackupService(
            Options.Create(new BackupOptions { Directory = _dir, RetentionDays = 7, MaxFiles = 0 }),
            config, NullLogger<BackupService>.Instance);

        var velho = CreateBackupFile("hospitalaccess-20260101-000000.zip");
        File.SetCreationTimeUtc(velho, DateTime.UtcNow.AddDays(-30));
        var novo = CreateBackupFile("hospitalaccess-20260826-120000.zip");
        File.SetCreationTimeUtc(novo, DateTime.UtcNow.AddDays(-1));

        var removed = service.ApplyRetention();

        Assert.Equal(1, removed);
        var remaining = Assert.Single(service.List());
        Assert.Equal("hospitalaccess-20260826-120000.zip", remaining.FileName);
    }

    [Fact]
    public void Retencao_ZeroEmAmbos_NaoRemoveNada()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = "Host=localhost;Database=x;Username=u;Password=p",
        }).Build();
        var service = new BackupService(
            Options.Create(new BackupOptions { Directory = _dir, RetentionDays = 0, MaxFiles = 0 }),
            config, NullLogger<BackupService>.Instance);

        var antigo = CreateBackupFile("hospitalaccess-20200101-000000.zip");
        File.SetCreationTimeUtc(antigo, DateTime.UtcNow.AddYears(-5));

        Assert.Equal(0, service.ApplyRetention());
        Assert.Single(service.List());
    }
}
