namespace HospitalAccess.Api.Options;

/// <summary>
/// Cópia de segurança do sistema. Era a única lacuna com perda IRREVERSÍVEL: todo o cadastro de
/// pessoas, as fotos de rosto, as permissões por porta e o histórico de acessos viviam apenas no
/// banco, sem nenhuma cópia automática.
/// </summary>
public sealed class BackupOptions
{
    public const string SectionName = "Backup";

    /// <summary>
    /// Rotina automática ligada. Ligada por padrão de propósito — um backup que depende de alguém
    /// lembrar de configurar é o backup que não existe no dia em que o disco falha.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Onde os arquivos ficam. Deve estar FORA do disco do banco (idealmente em volume/rede
    /// separado) — cópia no mesmo disco não protege contra a falha mais comum.
    /// </summary>
    public string Directory { get; set; } = "/var/lib/hospitalaccess/backups";

    /// <summary>Intervalo entre cópias automáticas.</summary>
    public int IntervalHours { get; set; } = 24;

    /// <summary>Por quantos dias guardar. 0 = nunca expurgar.</summary>
    public int RetentionDays { get; set; } = 14;

    /// <summary>
    /// Teto de arquivos guardados, independente da idade — trava de segurança para o disco não
    /// encher se a rotina rodar mais vezes que o previsto. 0 = sem teto.
    /// </summary>
    public int MaxFiles { get; set; } = 60;

    /// <summary>Caminho do <c>pg_dump</c>. O padrão resolve pelo PATH do serviço.</summary>
    public string PgDumpPath { get; set; } = "pg_dump";

    /// <summary>Teto de duração de uma cópia, para um pg_dump travado não segurar a rotina para sempre.</summary>
    public int TimeoutMinutes { get; set; } = 30;
}
