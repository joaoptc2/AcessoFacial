namespace HospitalAccess.Domain.Entities;

/// <summary>
/// Configurações globais do sistema (linha única). Hoje concentra as políticas de retenção de
/// dados (LGPD), configuráveis pela tela de Configurações. Um valor 0 significa "reter
/// indefinidamente" (sem expurgo automático).
/// </summary>
public class SystemSettings
{
    /// <summary>Chave fixa da linha única de configuração.</summary>
    public static readonly Guid SingletonId = new("00000000-0000-0000-0000-0000000000AA");

    public Guid Id { get; set; } = SingletonId;

    /// <summary>Dias para reter fotos de evento capturadas pelos controladores. 0 = nunca expurgar.</summary>
    public int EventPhotoRetentionDays { get; set; } = 90;

    /// <summary>Dias para reter o log de acessos. 0 = nunca expurgar (padrão: auditoria hospitalar exige guarda longa).</summary>
    public int AccessLogRetentionDays { get; set; }

    /// <summary>Dias para reter o log de alarmes. 0 = nunca expurgar.</summary>
    public int AlarmLogRetentionDays { get; set; }

    /// <summary>Dias para reter a trilha de auditoria de comandos de porta. 0 = nunca expurgar.</summary>
    public int ControllerAuditRetentionDays { get; set; }

    /// <summary>Última atualização das configurações.</summary>
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Username que atualizou as configurações por último.</summary>
    public string? UpdatedByUsername { get; set; }
}
