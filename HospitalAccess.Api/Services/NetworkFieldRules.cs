using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using HospitalAccess.Gateway;

namespace HospitalAccess.Api.Services;

/// <summary>
/// Validação da escrita de rede no aparelho (WriteTCPSetting). Valores errados aqui tornam o
/// controlador INALCANÇÁVEL (só se recupera pelo painel local) — as regras espelham as do demo
/// oficial do fabricante: MAC no formato AA-BB-CC-DD-EE-FF e IPs IPv4 válidos. O protocolo
/// ainda define que IP 0.0.0.0 RESETA para o padrão de fábrica 192.168.1.150 — recusado.
/// </summary>
public static partial class NetworkFieldRules
{
    [GeneratedRegex("^([A-Fa-f0-9]{2}-){5}[A-Fa-f0-9]{2}$")]
    private static partial Regex MacRegex();

    /// <summary>Null = válido; senão a mensagem de erro para o operador.</summary>
    public static string? Validate(ControllerNetworkInfo info)
    {
        if (!MacRegex().IsMatch(info.Mac ?? string.Empty))
            return "MAC inválido — use o formato AA-BB-CC-DD-EE-FF (hexadecimal separado por hífens).";
        if (!IsIpv4(info.Ip))
            return "IP inválido — informe um endereço IPv4 (ex.: 192.168.19.21).";
        if (info.Ip == "0.0.0.0")
            return "IP 0.0.0.0 não é permitido: o protocolo interpreta como reset para o padrão de fábrica (192.168.1.150).";
        if (!IsIpv4(info.IpMask))
            return "Máscara inválida — informe uma máscara IPv4 (ex.: 255.255.255.0).";
        if (!IsIpv4(info.IpGateway))
            return "Gateway inválido — informe um endereço IPv4.";
        if (!string.IsNullOrWhiteSpace(info.Dns) && !IsIpv4(info.Dns))
            return "DNS inválido — informe um endereço IPv4 ou deixe em branco.";
        if (!string.IsNullOrWhiteSpace(info.DnsBackup) && !IsIpv4(info.DnsBackup))
            return "DNS secundário inválido — informe um endereço IPv4 ou deixe em branco.";
        return null;
    }

    private static bool IsIpv4(string? value) =>
        IPAddress.TryParse(value, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork;
}
