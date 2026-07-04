namespace HospitalAccess.Infrastructure.Devices;

/// <summary>
/// Configuração da integração HTTP com o painel web dos controladores (seção "Device" do appsettings).
/// A senha do admin do painel web costuma ser a mesma nos 30 aparelhos: define-se aqui o padrão global
/// e cada <see cref="HospitalAccess.Domain.Entities.Controller.ApiPassword"/> só é usada se preenchida.
/// </summary>
public sealed class DeviceHttpOptions
{
    public const string SectionName = "Device";

    /// <summary>Senha padrão do painel web usada quando o controlador não tem uma própria. Cifrada em repouso não se aplica aqui (vem de config protegida do host).</summary>
    public string? DefaultApiPassword { get; set; }

    /// <summary>Caminho do login. Padrão do FC-8190H: <c>/api/User/Login</c>. Configurável para firmwares divergentes.</summary>
    public string LoginPath { get; set; } = "/api/User/Login";

    /// <summary>Timeout por request HTTP ao aparelho (ms).</summary>
    public int TimeoutMs { get; set; } = 30000;
}
