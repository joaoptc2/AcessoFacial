using System.Net;

namespace HospitalAccess.Api.Services;

/// <summary>
/// Descobre o IP REAL do cliente quando a aplicação está atrás de um proxy reverso (Nginx) e/ou
/// de um túnel (cloudflared).
///
/// <para>
/// POR QUE ISTO EXISTE: sem esta resolução, <c>HttpContext.Connection.RemoteIpAddress</c> é o
/// endereço do PROXY — sempre <c>127.0.0.1</c> na topologia documentada. Três coisas dependem do
/// IP de verdade e ficavam quebradas:
/// </para>
/// <list type="bullet">
/// <item>o rate limit do login é particionado por IP; com todos caindo na mesma partição, as 10
/// tentativas por minuto passam a valer para o HOSPITAL INTEIRO — um atacante tranca o login de
/// todo mundo, e uma troca de turno pode bater no limite sozinha;</item>
/// <item>o rate limit do phone-home (120/min) vira 120/min para os 30 aparelhos somados, o que em
/// horário de pico começaria a RECUSAR registro de acesso legítimo;</item>
/// <item>a conferência de procedência do phone-home (<see cref="DeviceCallbackRules"/>) nunca
/// casaria com o IP cadastrado do controlador.</item>
/// </list>
///
/// <para>
/// SEGURANÇA: cabeçalho de proxy é entrada do cliente e só vale se vier de um proxy que NÓS
/// confiamos. Por isso a resolução só acontece quando o peer da conexão está na lista de proxies
/// confiáveis; vindo de qualquer outro lugar, o endereço do socket é usado como está e os
/// cabeçalhos são ignorados. Sem essa checagem, qualquer um forjaria <c>X-Forwarded-For</c> para
/// escapar do rate limit ou se passar por um controlador.
/// </para>
/// </summary>
public static class ClientIpResolver
{
    /// <summary>
    /// Resolve o IP do cliente. <paramref name="peer"/> é o endereço do socket;
    /// <paramref name="cfConnectingIp"/> e <paramref name="xForwardedFor"/> são os cabeçalhos
    /// recebidos (podem ser nulos).
    /// </summary>
    public static IPAddress? Resolve(
        IPAddress? peer,
        string? cfConnectingIp,
        string? xForwardedFor,
        IReadOnlyCollection<IPAddress> trustedProxies)
    {
        if (peer is null) return null;
        if (!IsTrusted(peer, trustedProxies)) return peer; // origem não confiável: ignora cabeçalhos

        // CF-Connecting-IP primeiro: a Cloudflare SOBRESCREVE esse cabeçalho na borda, então ele
        // é o IP verdadeiro do cliente e não carrega a cadeia de saltos do X-Forwarded-For.
        if (TryParseAddress(cfConnectingIp, out var cloudflareClient)) return cloudflareClient;

        // Sem Cloudflare (acesso interno pelo Nginx), cai no X-Forwarded-For. A cadeia é
        // "cliente, proxy1, proxy2..." e cada salto ANEXA à direita — então o último endereço NÃO
        // confiável, varrendo da direita para a esquerda, é o cliente real. Pegar o primeiro da
        // lista seria confiar num valor que o próprio cliente pode ter enviado.
        if (!string.IsNullOrWhiteSpace(xForwardedFor))
        {
            var hops = xForwardedFor.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            for (var i = hops.Length - 1; i >= 0; i--)
            {
                if (!TryParseAddress(hops[i], out var hop)) continue;
                if (!IsTrusted(hop, trustedProxies)) return hop;
            }
        }

        return peer;
    }

    /// <summary>Lê a lista de proxies confiáveis da configuração, descartando entradas inválidas.</summary>
    public static IReadOnlyCollection<IPAddress> ParseTrustedProxies(IEnumerable<string>? configured)
    {
        var parsed = (configured ?? [])
            .Select(value => TryParseAddress(value, out var ip) ? ip : null)
            .Where(ip => ip is not null)
            .Select(ip => ip!)
            .ToList();

        // Padrão: o proxy roda na mesma máquina (Nginx e/ou cloudflared em loopback).
        return parsed.Count > 0 ? parsed : [IPAddress.Loopback, IPAddress.IPv6Loopback];
    }

    private static bool IsTrusted(IPAddress candidate, IReadOnlyCollection<IPAddress> trustedProxies) =>
        IsTrustedProxy(candidate, trustedProxies);

    /// <summary>
    /// O endereço está na lista de proxies confiáveis? Público porque o middleware também precisa
    /// decidir se aceita o <c>X-Forwarded-Proto</c> daquela origem.
    /// </summary>
    public static bool IsTrustedProxy(IPAddress? candidate, IReadOnlyCollection<IPAddress> trustedProxies) =>
        candidate is not null && trustedProxies.Any(trusted => Normalize(trusted).Equals(Normalize(candidate)));

    /// <summary>
    /// Aceita "1.2.3.4", "[::1]" e também a forma com porta que alguns proxies anexam
    /// ("1.2.3.4:5678"). IPv6 sem colchetes e com porta é ambíguo e fica de fora de propósito.
    /// </summary>
    private static bool TryParseAddress(string? value, out IPAddress address)
    {
        address = IPAddress.None;
        if (string.IsNullOrWhiteSpace(value)) return false;

        var text = value.Trim();

        // "[::1]:443" ou "[::1]"
        if (text.StartsWith('['))
        {
            var close = text.IndexOf(']');
            if (close > 1) text = text[1..close];
        }
        else if (text.Count(c => c == ':') == 1)
        {
            // Um único ':' só acontece em IPv4 com porta — IPv6 tem vários.
            text = text[..text.IndexOf(':')];
        }

        if (!IPAddress.TryParse(text, out var parsed)) return false;

        address = Normalize(parsed);
        return true;
    }

    /// <summary>
    /// IPv4 mapeado em IPv6 (<c>::ffff:1.2.3.4</c>) vira IPv4. O Kestrel reporta nessa forma em
    /// socket dual-stack; sem normalizar, o mesmo cliente contaria como dois IPs diferentes no
    /// rate limit e nunca casaria com o IP cadastrado de um controlador.
    /// </summary>
    private static IPAddress Normalize(IPAddress ip) =>
        ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;
}
