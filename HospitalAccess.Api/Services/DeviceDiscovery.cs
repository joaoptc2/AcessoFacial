using HospitalAccess.Gateway;

namespace HospitalAccess.Api.Services;

/// <summary>
/// Varredura UDP em múltiplas portas. O 8190H responde à busca na porta UDP LOCAL configurada
/// NELE — padrão de fábrica 8101 (é a porta que o demo oficial usa); este sistema historicamente
/// varria só 60000, e aparelho de fábrica nunca era encontrado ("Detectar SN" falhava no
/// cadastro). Varre as portas em sequência (o connector de broadcast faz bind local por porta)
/// e mescla os resultados por SN.
/// </summary>
public static class DeviceDiscovery
{
    /// <summary>Fábrica (8101) primeiro; 60000 cobre aparelhos reconfigurados por instalações antigas.</summary>
    public static readonly int[] DefaultPorts = { 8101, 60000 };

    public static async Task<IReadOnlyList<DiscoveredController>> SweepAsync(
        IDeviceGateway gateway, IEnumerable<int> udpPorts, TimeSpan scanPerPort, CancellationToken ct)
    {
        var merged = new List<DiscoveredController>();
        foreach (var port in udpPorts.Distinct())
        {
            var found = await gateway.DiscoverControllersAsync(port, scanPerPort, ct);
            foreach (var device in found)
                if (!merged.Exists(m => m.SerialNumber == device.SerialNumber))
                    merged.Add(device);
        }
        return merged;
    }
}
