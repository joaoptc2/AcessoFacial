using HospitalAccess.Gateway;

namespace HospitalAccess.Api.Services;

/// <summary>
/// Regras puras da relocalização por SN: dado o cadastro (SN + IP) e o resultado de uma
/// varredura UDP, decide se o aparelho não respondeu, se está no IP cadastrado, ou se MUDOU
/// de IP (MAC aleatório dos 8190H + DHCP → o IP troca no reboot e o cadastro fica órfão).
/// </summary>
public static class RelocateRules
{
    public enum Outcome { NotFound, SameIp, Moved }

    public sealed record Result(Outcome Outcome, string? DiscoveredIp);

    public static Result Match(string registeredSerialNumber, string registeredIp, IEnumerable<DiscoveredController> discovered)
    {
        // A varredura já deduplica por SN (primeiro a responder vence); aqui o FirstOrDefault
        // mantém a mesma semântica caso a lista venha de outra fonte.
        var match = discovered.FirstOrDefault(d =>
            string.Equals(d.SerialNumber, registeredSerialNumber, StringComparison.Ordinal));
        if (match is null || string.IsNullOrWhiteSpace(match.IpAddress))
            return new Result(Outcome.NotFound, null);

        return string.Equals(match.IpAddress, registeredIp, StringComparison.Ordinal)
            ? new Result(Outcome.SameIp, match.IpAddress)
            : new Result(Outcome.Moved, match.IpAddress);
    }

    /// <summary>ApiBaseUrl derivada do IP antigo (http://IP) acompanha o IP novo; URL customizada (ou vazia) fica como está.</summary>
    public static string FollowDerivedApiBaseUrl(string apiBaseUrl, string oldIp, string newIp) =>
        apiBaseUrl.TrimEnd('/') == $"http://{oldIp}" ? $"http://{newIp}" : apiBaseUrl;
}
