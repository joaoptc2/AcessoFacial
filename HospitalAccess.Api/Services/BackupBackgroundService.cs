using HospitalAccess.Api.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HospitalAccess.Api.Services;

/// <summary>
/// Rotina automática de cópia de segurança. Roda uma vez na subida (para que uma instalação nova
/// tenha cópia desde o primeiro dia, sem esperar o primeiro ciclo) e depois no intervalo
/// configurado, aplicando a retenção a cada passagem.
/// </summary>
public sealed class BackupBackgroundService : BackgroundService
{
    private readonly BackupService _backup;
    private readonly BackupOptions _options;
    private readonly RuntimeSettingsProvider _settings;
    private readonly ILogger<BackupBackgroundService> _logger;

    public BackupBackgroundService(BackupService backup, IOptions<BackupOptions> options,
        RuntimeSettingsProvider settings, ILogger<BackupBackgroundService> logger)
    {
        _backup = backup;
        _options = options.Value;
        _settings = settings;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogWarning(
                "Cópia de segurança automática DESLIGADA (Backup:Enabled=false). Nenhuma cópia será gerada — " +
                "garanta que exista uma rotina externa, senão uma falha de disco é perda total e irreversível.");
            return;
        }

        // Sonda de escrita: sem isto, um diretório não-gravável só apareceria na primeira tentativa
        // de cópia, de madrugada, no log que ninguém está lendo.
        if (!_backup.TryPrepareDirectory(out var dirError))
        {
            _logger.LogError(
                "Cópia de segurança INDISPONÍVEL: o diretório '{Dir}' não é gravável ({Error}). " +
                "Correção: sudo install -d -o <usuario-do-servico> -g <grupo> {Dir} — ou aponte Backup:Directory " +
                "para um caminho gravável, de preferência em disco/volume separado do banco.",
                _backup.Directory, dirError, _backup.Directory);
            return;
        }

        // O intervalo é lido A CADA VOLTA, e não uma vez na subida: mudar o valor na tela de
        // Configurações passa a valer na próxima cópia, sem reiniciar o serviço.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var file = await _backup.CreateAsync(stoppingToken);
                var politica = _settings.Backup;
                var removed = _backup.ApplyRetention(politica.MaxFiles, politica.RetentionDays);
                if (removed > 0)
                    _logger.LogInformation("Retenção de cópias: {Removed} arquivo(s) antigo(s) removido(s).", removed);

                _logger.LogInformation(
                    "Cópia automática concluída: {File} ({Size:N0} bytes).", file.FileName, file.SizeBytes);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Falha de cópia não pode derrubar o serviço, mas precisa gritar: é o único item
                // do sistema cuja ausência causa perda irreversível.
                _logger.LogError(ex, "FALHA NA CÓPIA DE SEGURANÇA automática — os dados estão sem cópia nova.");
            }

            var horas = Math.Max(1, _settings.Backup.IntervalHours);
            try
            {
                await Task.Delay(TimeSpan.FromHours(horas), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
