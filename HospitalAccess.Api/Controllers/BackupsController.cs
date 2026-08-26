using HospitalAccess.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HospitalAccess.Api.Controllers;

/// <summary>
/// Cópias de segurança: listar, gerar sob demanda, BAIXAR e remover.
///
/// <para>
/// ⚠️ Só Admin, e por um motivo forte: o arquivo contém o banco inteiro (nomes, documentos, FOTOS
/// DE ROSTO) e o chaveiro que decifra as senhas dos controladores. Baixar uma cópia é, na prática,
/// levar o sistema inteiro para fora do servidor.
/// </para>
/// </summary>
[ApiController]
[Route("api/backups")]
[Authorize(Roles = "Admin")]
public sealed class BackupsController : ControllerBase
{
    private readonly BackupService _backup;
    private readonly SingleFlight _singleFlight;
    private readonly ILogger<BackupsController> _logger;

    public BackupsController(BackupService backup, SingleFlight singleFlight, ILogger<BackupsController> logger)
    {
        _backup = backup;
        _singleFlight = singleFlight;
        _logger = logger;
    }

    /// <summary>Cópias disponíveis, da mais recente para a mais antiga.</summary>
    [HttpGet]
    public IActionResult List() => Ok(new
    {
        Directory = _backup.Directory,
        Writable = _backup.TryPrepareDirectory(out _),
        Files = _backup.List(),
    });

    /// <summary>
    /// Gera uma cópia AGORA. Síncrono de propósito: quem clicou quer o arquivo pronto para baixar
    /// em seguida, e um pg_dump deste porte termina em segundos a poucos minutos (o teto duro de
    /// Backup:TimeoutMinutes impede que segure a requisição para sempre).
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create(CancellationToken ct)
    {
        // Duas cópias simultâneas dobram a carga no banco e podem estourar o disco à toa.
        const string flightKey = "backup:create";
        if (!_singleFlight.TryBegin(flightKey))
            return Conflict(new { error = "Já existe uma cópia de segurança sendo gerada." });

        try
        {
            var file = await _backup.CreateAsync(ct);
            _backup.ApplyRetention();
            _logger.LogInformation("Cópia de segurança gerada sob demanda por {User}.", User.Identity?.Name);
            return Ok(file);
        }
        catch (InvalidOperationException ex)
        {
            // Causa conhecida e acionável (pg_dump ausente, timeout, banco não configurado):
            // devolve a mensagem para a tela em vez de um 500 opaco.
            _logger.LogError(ex, "Falha ao gerar cópia de segurança sob demanda.");
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = ex.Message });
        }
        finally
        {
            _singleFlight.End(flightKey);
        }
    }

    /// <summary>
    /// Baixa uma cópia. O nome é validado contra um padrão estrito e o caminho resolvido é
    /// conferido contra o diretório de backups — o parâmetro da rota não pode escapar para
    /// outro lugar do servidor.
    /// </summary>
    [HttpGet("{fileName}/download")]
    public IActionResult Download(string fileName)
    {
        var path = _backup.ResolveForDownload(fileName);
        if (path is null) return NotFound();

        _logger.LogWarning(
            "Cópia de segurança {File} BAIXADA por {User} ({Ip}) — o arquivo contém dados pessoais e as chaves de criptografia.",
            fileName, User.Identity?.Name, HttpContext.Connection.RemoteIpAddress);

        return PhysicalFile(path, "application/zip", fileName);
    }

    [HttpDelete("{fileName}")]
    public IActionResult Delete(string fileName)
    {
        if (!_backup.TryDelete(fileName)) return NotFound();
        _logger.LogWarning("Cópia de segurança {File} removida por {User}.", fileName, User.Identity?.Name);
        return NoContent();
    }
}
