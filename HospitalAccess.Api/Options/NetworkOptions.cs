namespace HospitalAccess.Api.Options;

/// <summary>
/// Rede e proxy reverso. Relevante quando a aplicação NÃO recebe a conexão direto do cliente —
/// isto é, sempre que há Nginx na frente (topologia padrão do guia de instalação) e/ou o túnel
/// da Cloudflare para acesso remoto.
/// </summary>
public sealed class NetworkOptions
{
    public const string SectionName = "Network";

    /// <summary>
    /// Endereços cujos cabeçalhos de proxy (<c>CF-Connecting-IP</c>, <c>X-Forwarded-For</c>,
    /// <c>X-Forwarded-Proto</c>) são aceitos. Vazio = loopback (IPv4 e IPv6), que cobre Nginx e
    /// cloudflared rodando na mesma máquina.
    ///
    /// <para>
    /// Só amplie se um proxy estiver em OUTRO host. Colocar aqui um endereço alcançável por
    /// terceiros permite que eles forjem o IP de origem — e o IP de origem governa o rate limit
    /// do login e a conferência de procedência do phone-home dos aparelhos.
    /// </para>
    /// </summary>
    public string[] TrustedProxies { get; set; } = [];
}
