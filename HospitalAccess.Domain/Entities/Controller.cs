namespace HospitalAccess.Domain.Entities;

/// <summary>Um dos 30 controladores faciais 8190H (tipo A33_Face).</summary>
public class Controller
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;

    public string IpAddress { get; set; } = string.Empty;
    public int Port { get; set; }

    /// <summary>SN de 16 dígitos exigido pelo SDK.</summary>
    public string SerialNumber { get; set; } = string.Empty;

    /// <summary>Senha de comunicação. NUNCA versionar; carregar de config protegida.</summary>
    public string CommunicationPassword { get; set; } = string.Empty;

    /// <summary>Firmware &gt;= 4.28 habilita WaitRepeatMessage no upload de face.</summary>
    public bool SupportsWaitRepeatMessage { get; set; }

    /// <summary>Timeout por comando (ms). Configurável por controlador (latência de rede varia entre os 30).</summary>
    public int TimeoutMs { get; set; } = 3000;

    /// <summary>Tentativas de retry por comando antes de marcar falha.</summary>
    public int RestartCount { get; set; } = 3;

    public ICollection<Door> Doors { get; set; } = new List<Door>();
}
