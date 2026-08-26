using System.Net;

namespace HospitalAccess.Api.Services;

/// <summary>
/// Regras puras do phone-home dos aparelhos (ver DeviceCallbackController). Extraídas para
/// static porque o endpoint é anônimo por limitação do firmware — a lógica que decide se um
/// evento é plausível precisa ser testável sem subir o pipeline HTTP.
/// </summary>
public static class DeviceCallbackRules
{
    /// <summary>
    /// O IP de origem da requisição corresponde ao IP cadastrado do controlador?
    /// <para>
    /// Permissivo por decisão consciente — devolve <c>true</c> (não bloqueia) quando não há o
    /// que comparar: cadastro sem IP, origem desconhecida (ex.: pipe em memória nos testes) ou
    /// IP cadastrado que não é um endereço válido. Um cadastro incompleto não pode virar recusa
    /// silenciosa de evento de acesso; quem decide recusar é a chave de configuração no chamador.
    /// </para>
    /// <para>
    /// Kestrel costuma reportar IPv4 na forma mapeada em IPv6 (<c>::ffff:192.168.1.5</c>), então
    /// os dois lados são normalizados antes da comparação — sem isso, todo evento legítimo de uma
    /// rede IPv4 apareceria como divergente.
    /// </para>
    /// </summary>
    public static bool SourceIpMatches(IPAddress? remote, string? registeredIp)
    {
        if (string.IsNullOrWhiteSpace(registeredIp)) return true;
        if (remote is null) return true;
        if (!IPAddress.TryParse(registeredIp.Trim(), out var expected)) return true;

        return Normalize(remote).Equals(Normalize(expected));
    }

    private static IPAddress Normalize(IPAddress ip) =>
        ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;
}
