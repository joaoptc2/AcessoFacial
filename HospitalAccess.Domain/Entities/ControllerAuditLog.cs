namespace HospitalAccess.Domain.Entities;

/// <summary>
/// Trilha de auditoria dos comandos operacionais enviados a um controlador (abrir, fechar,
/// manter aberta, trancar, destrancar, sincronizar relógio, limpar alarme etc.). Registra
/// QUEM executou QUAL comando em QUAL porta e QUANDO — requisito de auditoria hospitalar.
/// Append-only, mesmo espírito de AccessLog/UserAuditLog.
/// </summary>
public class ControllerAuditLog
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;

    public Guid ControllerId { get; set; }
    public string ControllerName { get; set; } = string.Empty;

    /// <summary>Ação executada (ex.: "AbrirPorta", "TrancarPorta", "SincronizarRelógio").</summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>Username do StaffUser que disparou o comando (null se sistema/automação).</summary>
    public string? PerformedByUsername { get; set; }

    /// <summary>Resultado: true se o controlador confirmou; false em falha/timeout.</summary>
    public bool Success { get; set; }

    /// <summary>Mensagem de erro quando Success = false.</summary>
    public string? Error { get; set; }
}
