using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using HospitalAccess.Api.Options;
using Microsoft.Extensions.Options;
using Npgsql;

namespace HospitalAccess.Api.Services;

/// <summary>Arquivo de cópia de segurança disponível para download.</summary>
public sealed record BackupFileInfo(string FileName, long SizeBytes, DateTime CreatedAtUtc);

/// <summary>
/// Gera e administra as cópias de segurança.
///
/// <para>
/// O QUE ENTRA NA CÓPIA — e por que os dois juntos: o <c>pg_dump</c> do banco E o chaveiro da
/// DataProtection. As senhas dos controladores são cifradas em repouso com esse chaveiro; restaurar
/// só o banco devolve senhas INDECIFRÁVEIS e nenhum aparelho volta a funcionar. É o erro clássico
/// desse desenho, então o chaveiro vai dentro do mesmo arquivo, sem depender de ninguém lembrar.
/// </para>
/// <para>
/// ⚠️ O arquivo gerado contém dados pessoais (nomes, documentos, FOTOS DE ROSTO) e as chaves de
/// criptografia. Trate-o com o mesmo cuidado do banco: diretório restrito e transporte controlado.
/// </para>
/// </summary>
public sealed class BackupService
{
    /// <summary>
    /// Nome de arquivo aceito no download. Estrito de propósito: é o que impede que o parâmetro
    /// da rota escape do diretório de backups (path traversal) — nada de barra, ponto-ponto ou
    /// caminho absoluto passa por aqui.
    /// </summary>
    private static readonly Regex FileNamePattern =
        new(@"^hospitalaccess-\d{8}-\d{6}\.zip$", RegexOptions.Compiled);

    private readonly BackupOptions _options;
    private readonly string _connectionString;
    private readonly string _dataProtectionKeysPath;
    private readonly ILogger<BackupService> _logger;

    public BackupService(IOptions<BackupOptions> options, IConfiguration config, ILogger<BackupService> logger)
    {
        _options = options.Value;
        _logger = logger;
        _connectionString = config.GetConnectionString("Postgres") ?? string.Empty;

        var keysPath = config["DataProtection:KeysPath"];
        _dataProtectionKeysPath = string.IsNullOrWhiteSpace(keysPath)
            ? "/var/lib/hospitalaccess/dpkeys"
            : keysPath;
    }

    public string Directory => _options.Directory;

    /// <summary>Sonda de escrita no boot: um diretório não-gravável só falharia na primeira cópia, de madrugada.</summary>
    public bool TryPrepareDirectory(out string? error)
    {
        try
        {
            System.IO.Directory.CreateDirectory(_options.Directory);
            var probe = Path.Combine(_options.Directory, ".write-probe");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    /// <summary>Cópias existentes, da mais recente para a mais antiga.</summary>
    public IReadOnlyList<BackupFileInfo> List()
    {
        if (!System.IO.Directory.Exists(_options.Directory)) return [];

        return new DirectoryInfo(_options.Directory)
            .GetFiles("hospitalaccess-*.zip")
            .Where(f => FileNamePattern.IsMatch(f.Name))
            .OrderByDescending(f => f.CreationTimeUtc)
            .Select(f => new BackupFileInfo(f.Name, f.Length, f.CreationTimeUtc))
            .ToList();
    }

    /// <summary>
    /// Resolve o caminho de um arquivo para download. Devolve null quando o nome não casa com o
    /// padrão ou quando o caminho resolvido cai FORA do diretório de backups — as duas checagens
    /// são intencionalmente redundantes, porque o custo de errar aqui é servir um arquivo
    /// arbitrário do servidor.
    /// </summary>
    public string? ResolveForDownload(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || !FileNamePattern.IsMatch(fileName)) return null;

        var root = Path.GetFullPath(_options.Directory);
        var full = Path.GetFullPath(Path.Combine(root, fileName));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return null;

        return File.Exists(full) ? full : null;
    }

    public bool TryDelete(string fileName)
    {
        var path = ResolveForDownload(fileName);
        if (path is null) return false;
        File.Delete(path);
        return true;
    }

    /// <summary>
    /// Gera uma cópia completa e devolve o arquivo criado. Lança <see cref="InvalidOperationException"/>
    /// com mensagem legível quando o <c>pg_dump</c> não está disponível ou falha.
    /// </summary>
    public async Task<BackupFileInfo> CreateAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
            throw new InvalidOperationException("ConnectionStrings:Postgres não configurada — impossível gerar a cópia.");

        System.IO.Directory.CreateDirectory(_options.Directory);

        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var fileName = $"hospitalaccess-{stamp}.zip";
        var finalPath = Path.Combine(_options.Directory, fileName);
        var staging = Path.Combine(Path.GetTempPath(), $"ha-backup-{Guid.NewGuid():N}");

        System.IO.Directory.CreateDirectory(staging);
        try
        {
            var dumpPath = Path.Combine(staging, "database.dump");
            await RunPgDumpAsync(dumpPath, ct);

            CopyDataProtectionKeys(staging);
            await File.WriteAllTextAsync(Path.Combine(staging, "LEIA-ME.txt"), BuildRestoreInstructions(), Encoding.UTF8, ct);

            // Escreve num temporário e move por cima: ninguém baixa um zip pela metade, e a
            // rotina de retenção nunca vê um arquivo em construção.
            var tempZip = finalPath + ".tmp";
            if (File.Exists(tempZip)) File.Delete(tempZip);
            ZipFile.CreateFromDirectory(staging, tempZip, CompressionLevel.Optimal, includeBaseDirectory: false);
            File.Move(tempZip, finalPath, overwrite: true);
        }
        finally
        {
            try { System.IO.Directory.Delete(staging, recursive: true); }
            catch (Exception ex) { _logger.LogWarning(ex, "Não foi possível limpar o diretório temporário da cópia ({Dir}).", staging); }
        }

        var info = new FileInfo(finalPath);
        _logger.LogInformation("Cópia de segurança gerada: {File} ({Size:N0} bytes).", fileName, info.Length);
        return new BackupFileInfo(fileName, info.Length, info.CreationTimeUtc);
    }

    /// <summary>Aplica a retenção (idade e teto de arquivos). Devolve quantos foram removidos.</summary>
    public int ApplyRetention()
    {
        var files = List();
        var toDelete = new List<BackupFileInfo>();

        if (_options.RetentionDays > 0)
        {
            var cutoff = DateTime.UtcNow.AddDays(-_options.RetentionDays);
            toDelete.AddRange(files.Where(f => f.CreatedAtUtc < cutoff));
        }

        if (_options.MaxFiles > 0 && files.Count > _options.MaxFiles)
            toDelete.AddRange(files.Skip(_options.MaxFiles));

        var removed = 0;
        foreach (var file in toDelete.DistinctBy(f => f.FileName))
        {
            try
            {
                if (TryDelete(file.FileName)) removed++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Não foi possível remover a cópia antiga {File}.", file.FileName);
            }
        }

        return removed;
    }

    // ------------------------------------------------------------------ //

    private async Task RunPgDumpAsync(string outputPath, CancellationToken ct)
    {
        var csb = new NpgsqlConnectionStringBuilder(_connectionString);

        var psi = new ProcessStartInfo
        {
            FileName = _options.PgDumpPath,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        // Formato custom (-Fc): comprimido e restaurável seletivamente com pg_restore.
        psi.ArgumentList.Add("--format=custom");
        psi.ArgumentList.Add("--no-owner");
        psi.ArgumentList.Add("--no-acl");
        psi.ArgumentList.Add($"--file={outputPath}");
        psi.ArgumentList.Add($"--host={csb.Host}");
        psi.ArgumentList.Add($"--port={(csb.Port == 0 ? 5432 : csb.Port)}");
        psi.ArgumentList.Add($"--username={csb.Username}");
        psi.ArgumentList.Add($"--dbname={csb.Database}");

        // Senha por variável de ambiente do processo filho: não aparece na linha de comando
        // (visível a qualquer usuário do servidor por `ps`).
        if (!string.IsNullOrEmpty(csb.Password)) psi.Environment["PGPASSWORD"] = csb.Password;

        using var process = new Process { StartInfo = psi };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Não foi possível executar '{_options.PgDumpPath}' ({ex.Message}). " +
                "Instale o cliente do PostgreSQL no servidor (pacote postgresql-client) ou aponte " +
                "Backup:PgDumpPath para o caminho do executável.", ex);
        }

        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(Math.Max(1, _options.TimeoutMinutes)));

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* já morreu */ }
            throw new InvalidOperationException(
                $"pg_dump excedeu {_options.TimeoutMinutes} min e foi interrompido.");
        }

        var stderr = await stderrTask;
        await stdoutTask;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"pg_dump falhou (código {process.ExitCode}): {stderr.Trim()}");
        }
    }

    /// <summary>
    /// Copia o chaveiro da DataProtection para dentro da cópia. Ausência dele NÃO derruba o
    /// backup — o banco é o essencial — mas vira Warning, porque restaurar sem as chaves obriga a
    /// redigitar a senha de todos os controladores.
    /// </summary>
    private void CopyDataProtectionKeys(string staging)
    {
        try
        {
            if (!System.IO.Directory.Exists(_dataProtectionKeysPath))
            {
                _logger.LogWarning(
                    "Chaveiro da DataProtection não encontrado em {Path}: a cópia sai SEM as chaves e, ao restaurar, " +
                    "as senhas dos controladores precisarão ser redigitadas.", _dataProtectionKeysPath);
                return;
            }

            var target = System.IO.Directory.CreateDirectory(Path.Combine(staging, "dpkeys"));
            var keys = System.IO.Directory.GetFiles(_dataProtectionKeysPath, "*.xml");
            foreach (var key in keys)
                File.Copy(key, Path.Combine(target.FullName, Path.GetFileName(key)));

            if (keys.Length == 0)
                _logger.LogWarning("Chaveiro da DataProtection em {Path} está vazio.", _dataProtectionKeysPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha ao incluir o chaveiro da DataProtection na cópia (o banco foi copiado normalmente).");
        }
    }

    private string BuildRestoreInstructions() => $"""
        CÓPIA DE SEGURANÇA — Sistema de Controle de Acesso Hospitalar
        Gerada em {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC

        CONTEÚDO
          database.dump  Banco completo (pg_dump --format=custom).
          dpkeys/        Chaveiro da DataProtection.

        ⚠️ CONTÉM DADOS PESSOAIS (nomes, documentos, FOTOS DE ROSTO) E AS CHAVES DE
        CRIPTOGRAFIA. Guarde com o mesmo cuidado do banco de produção.

        POR QUE OS DOIS JUNTOS
        As senhas dos controladores são cifradas em repouso com o chaveiro. Restaurar só o
        banco devolve senhas INDECIFRÁVEIS e nenhum aparelho volta a funcionar.

        COMO RESTAURAR
          1. Pare o serviço:
               sudo systemctl stop hospitalaccess

          2. Restaure o chaveiro (ajuste o dono para o usuário do serviço):
               sudo unzip -o backup.zip 'dpkeys/*' -d /tmp/restore
               sudo cp /tmp/restore/dpkeys/*.xml {_dataProtectionKeysPath}/
               sudo chown -R <usuario-do-servico> {_dataProtectionKeysPath}

          3. Restaure o banco (em banco VAZIO — pg_restore não limpa sozinho):
               sudo -u postgres createdb hospital_access_restore
               pg_restore --no-owner --no-acl -d hospital_access_restore database.dump

          4. Aponte ConnectionStrings__Postgres para o banco restaurado e suba o serviço:
               sudo systemctl start hospitalaccess

          5. CONFIRME: entre no sistema, abra um controlador e use "Testar conexão".
             Se a senha do aparelho for aceita, o chaveiro foi restaurado corretamente.

        TESTE A RESTAURAÇÃO PERIODICAMENTE. Cópia que nunca foi restaurada é uma
        esperança, não um backup.
        """;
}
