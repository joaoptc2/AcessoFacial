using HospitalAccess.Api.Services;
using HospitalAccess.Application.Sync;
using HospitalAccess.Domain.Entities;
using HospitalAccess.Domain.Enums;
using HospitalAccess.Gateway;
using HospitalAccess.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HospitalAccess.Api.Controllers;

/// <summary>
/// Configuração de rede do controlador, descoberta por broadcast UDP e relocalização por número
/// de série. Existe por causa do MAC ALEATÓRIO do 8190H: em DHCP o IP troca no reboot e o
/// aparelho "some" do cadastro. Escrever IP errado torna o controlador inacessível até acesso
/// físico — por isso a validação acontece antes da gravação.
/// </summary>
public sealed class ControllerNetworkController : ControllerEndpointBase
{
    private readonly ILogger<ControllerNetworkController> _logger;

    public ControllerNetworkController(AccessDbContext db, IDeviceGateway gateway,
        ILogger<ControllerNetworkController> logger)
        : base(db, gateway)
    {
        _logger = logger;
    }

    [HttpGet("{id:guid}/network")]
    [Authorize(Roles = "Admin,Operator")]
    public Task<IActionResult> GetNetwork(Guid id, CancellationToken ct) => RunReadAsync(id, Gateway.ReadNetworkSettingsAsync, ct);

    /// <summary>
    /// Grava a configuração de rede NO APARELHO (WriteTCPSetting). Valida antes (valor errado =
    /// aparelho inalcançável) e, se o IP gravado for diferente do cadastrado, atualiza TAMBÉM o
    /// cadastro (IpAddress + ApiBaseUrl derivada) — antes o cadastro ficava órfão do IP novo e o
    /// aparelho "sumia" logo após a gravação.
    /// </summary>
    [HttpPut("{id:guid}/network")]
    [Authorize(Roles = "Admin,Operator")]
    public async Task<IActionResult> PutNetwork(Guid id, [FromBody] ControllerNetworkInfo info, CancellationToken ct)
    {
        var controller = await Db.Controllers.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (controller is null) return NotFound();

        if (NetworkFieldRules.Validate(info) is { } validationError)
            return BadRequest(new { error = validationError });

        try
        {
            await Gateway.WriteNetworkSettingsAsync(controller, info, ct);
        }
        catch (Exception ex)
        {
            return StatusCode(502, new { error = ex.Message });
        }

        if (!string.Equals(info.Ip, controller.IpAddress, StringComparison.Ordinal))
        {
            var oldIp = controller.IpAddress;
            controller.IpAddress = info.Ip;
            controller.ApiBaseUrl = RelocateRules.FollowDerivedApiBaseUrl(controller.ApiBaseUrl, oldIp, info.Ip);
            await Db.SaveChangesAsync(ct);
            await AuditAsync(controller, $"GravarRede {oldIp}→{info.Ip}", success: true, error: null, ct);
            _logger.LogInformation(
                "Rede do controlador {Name} gravada com IP novo — cadastro acompanhou: {OldIp} → {NewIp}.",
                controller.Name, oldIp, info.Ip);
        }
        else
        {
            await AuditAsync(controller, "GravarRede", success: true, error: null, ct);
        }

        return NoContent();
    }

    /// <summary>
    /// Varredura por broadcast UDP para descobrir controladores na rede local. Sem udpPort
    /// explícito, varre as portas padrão (8101 de fábrica + 60000 legado) e mescla por SN —
    /// aparelho recém-tirado da caixa escuta em 8101 e não aparecia na varredura antiga.
    /// </summary>
    [HttpPost("discover")]
    [Authorize(Roles = "Admin,Operator")]
    public async Task<IActionResult> Discover([FromQuery] int? udpPort = null, [FromQuery] int scanSeconds = 4, CancellationToken ct = default)
    {
        var ports = udpPort is { } p ? new[] { p } : DeviceDiscovery.DefaultPorts;
        var found = await DeviceDiscovery.SweepAsync(Gateway, ports, TimeSpan.FromSeconds(scanSeconds), ct);
        return Ok(found);
    }

    /// <summary>
    /// RELOCALIZA o controlador pelo SN: varredura UDP na rede e, se o aparelho responder com
    /// IP diferente do cadastrado, atualiza o cadastro (IP + ApiBaseUrl derivada). Saída para o
    /// MAC aleatório dos 8190H: quando o DHCP troca o IP no reboot, o cadastro se re-encontra
    /// sem edição manual. Limite físico: broadcast não cruza VLAN — o servidor precisa estar
    /// na mesma L2 dos aparelhos.
    /// </summary>
    [HttpPost("{id:guid}/relocate")]
    [Authorize(Roles = "Admin,Operator")]
    public async Task<IActionResult> Relocate(Guid id, [FromQuery] int? udpPort = null, [FromQuery] int scanSeconds = 4, CancellationToken ct = default)
    {
        var controller = await Db.Controllers.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (controller is null) return NotFound();

        var ports = udpPort is { } p ? new[] { p } : DeviceDiscovery.DefaultPorts;
        var found = await DeviceDiscovery.SweepAsync(Gateway, ports, TimeSpan.FromSeconds(scanSeconds), ct);
        var match = RelocateRules.Match(controller.SerialNumber, controller.IpAddress, found);

        switch (match.Outcome)
        {
            case RelocateRules.Outcome.NotFound:
                return StatusCode(502, new
                {
                    error = $"O aparelho SN {controller.SerialNumber} não respondeu à varredura UDP " +
                            $"({found.Count} outro(s) responderam). O broadcast só alcança a mesma VLAN/L2 — " +
                            "se o servidor está em outra rede, confira o aparelho fisicamente ou ajuste o IP na edição.",
                });

            case RelocateRules.Outcome.SameIp:
                return Ok(new { moved = false, ipAddress = controller.IpAddress, message = "O aparelho respondeu no IP já cadastrado — nada a corrigir." });

            default:
                var oldIp = controller.IpAddress;
                var newIp = match.DiscoveredIp!;
                controller.IpAddress = newIp;
                controller.ApiBaseUrl = RelocateRules.FollowDerivedApiBaseUrl(controller.ApiBaseUrl, oldIp, newIp);
                await Db.SaveChangesAsync(ct);
                await AuditAsync(controller, $"RelocalizarIP {oldIp}→{newIp}", success: true, error: null, ct);
                _logger.LogWarning(
                    "Controlador {Name} (SN {Sn}) relocalizado por varredura: IP {OldIp} → {NewIp} (cadastro atualizado).",
                    controller.Name, controller.SerialNumber, oldIp, newIp);
                return Ok(new { moved = true, oldIp, ipAddress = newIp, message = $"IP atualizado: {oldIp} → {newIp}." });
        }
    }
}
