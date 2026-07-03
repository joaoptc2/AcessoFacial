using HospitalAccess.Domain.Entities;
using HospitalAccess.Gateway;
using HospitalAccess.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HospitalAccess.Api.Controllers;

/// <summary>
/// Acionamento de emergência/evacuação em massa. Restrito a Admin e integralmente auditado
/// (ControllerAuditLog). Ativar: mantém TODAS as portas abertas e dispara o alarme de incêndio
/// em cada controlador. Desativar: fecha as portas e silencia os alarmes.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = "Admin")]
public class EmergencyController : ControllerBase
{
    private readonly AccessDbContext _db;
    private readonly IDeviceGateway _gateway;
    private readonly ILogger<EmergencyController> _logger;

    public EmergencyController(AccessDbContext db, IDeviceGateway gateway, ILogger<EmergencyController> logger)
    {
        _db = db;
        _gateway = gateway;
        _logger = logger;
    }

    public record EmergencyDeviceResult(Guid ControllerId, string ControllerName, bool Success, string? Error);

    /// <summary>EVACUAÇÃO: mantém todas as portas abertas e dispara o alarme de incêndio em todos os controladores.</summary>
    [HttpPost("activate")]
    public Task<IActionResult> Activate(CancellationToken ct) =>
        RunOnAllAsync("EmergênciaAtivar", async (controller, c) =>
        {
            await _gateway.HoldDoorOpenAsync(controller, c);
            await _gateway.TriggerFireAlarmAsync(controller, c);
        }, ct);

    /// <summary>Dispara SÓ o alarme de incêndio em TODOS os controladores (sem mexer nas portas).</summary>
    [HttpPost("fire-alarm")]
    public Task<IActionResult> FireAlarmAll(CancellationToken ct) =>
        RunOnAllAsync("IncêndioUniversal", (controller, c) => _gateway.TriggerFireAlarmAsync(controller, c), ct);

    /// <summary>Silencia/encerra os alarmes em TODOS os controladores (sem mexer nas portas).</summary>
    [HttpPost("clear-alarms")]
    public Task<IActionResult> ClearAlarmsAll(CancellationToken ct) =>
        RunOnAllAsync("SilenciarAlarmesUniversal", (controller, c) => _gateway.ClearAlarmAsync(controller, c), ct);

    /// <summary>Encerra a emergência: fecha as portas (sai do modo aberto) e silencia os alarmes.</summary>
    [HttpPost("deactivate")]
    public Task<IActionResult> Deactivate(CancellationToken ct) =>
        RunOnAllAsync("EmergênciaDesativar", async (controller, c) =>
        {
            await _gateway.CloseDoorAsync(controller, c);
            await _gateway.ClearAlarmAsync(controller, c);
        }, ct);

    private async Task<IActionResult> RunOnAllAsync(
        string action, Func<Domain.Entities.Controller, CancellationToken, Task> command, CancellationToken ct)
    {
        var controllers = await _db.Controllers.AsNoTracking().ToListAsync(ct);
        var performedBy = User.Identity?.Name;
        _logger.LogWarning("Comando de emergência {Action} disparado por {User} em {Count} controladores.",
            action, performedBy, controllers.Count);

        // Em paralelo: numa emergência, cada segundo conta e as portas devem liberar juntas.
        var results = await Task.WhenAll(controllers.Select(async controller =>
        {
            try
            {
                await command(controller, ct);
                return new EmergencyDeviceResult(controller.Id, controller.Name, true, null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Emergência {Action} falhou no controlador {Controller}.", action, controller.Name);
                return new EmergencyDeviceResult(controller.Id, controller.Name, false, ex.Message);
            }
        }));

        // Auditoria por dispositivo.
        foreach (var r in results)
        {
            _db.ControllerAuditLogs.Add(new ControllerAuditLog
            {
                ControllerId = r.ControllerId,
                ControllerName = r.ControllerName,
                Action = action,
                PerformedByUsername = performedBy,
                Success = r.Success,
                Error = r.Error,
            });
        }
        await _db.SaveChangesAsync(ct);

        return Ok(new
        {
            action,
            total = results.Length,
            succeeded = results.Count(r => r.Success),
            failed = results.Count(r => !r.Success),
            devices = results,
        });
    }
}
