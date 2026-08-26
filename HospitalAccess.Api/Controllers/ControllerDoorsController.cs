using HospitalAccess.Api.Services;
using HospitalAccess.Domain.Entities;
using HospitalAccess.Gateway;
using HospitalAccess.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HospitalAccess.Api.Controllers;

/// <summary>
/// Comandos remotos de porta. Cada controlador 8190H tem um único relé, então "controlador" e
/// "porta" são a mesma coisa. Todo comando entra na trilha de auditoria com quem disparou e o
/// resultado — é a resposta a "quem abriu esta porta às 3h da manhã".
/// </summary>
public sealed class ControllerDoorsController : ControllerEndpointBase
{
    public ControllerDoorsController(AccessDbContext db, IDeviceGateway gateway)
        : base(db, gateway)
    {
    }


    /// <summary>Abre a porta (pulso — volta a fechar após o tempo de liberação configurado no controlador).</summary>
    [HttpPost("{id:guid}/open")]
    [Authorize(Roles = "Admin,Operator,Reception")]
    public Task<IActionResult> Open(Guid id, CancellationToken ct) => RunDoorCommand(id, "AbrirPorta", Gateway.OpenDoorAsync, ct);

    /// <summary>Fecha a porta (encerra o modo "sempre aberto", se ativo).</summary>
    [HttpPost("{id:guid}/close")]
    [Authorize(Roles = "Admin,Operator,Reception")]
    public Task<IActionResult> Close(Guid id, CancellationToken ct) => RunDoorCommand(id, "FecharPorta", Gateway.CloseDoorAsync, ct);

    /// <summary>Mantém a porta aberta (modo "sempre aberto") até um comando de fechar ou trancar.</summary>
    [HttpPost("{id:guid}/hold-open")]
    [Authorize(Roles = "Admin,Operator")]
    public Task<IActionResult> HoldOpen(Guid id, CancellationToken ct) => RunDoorCommand(id, "ManterAberta", Gateway.HoldDoorOpenAsync, ct);

    /// <summary>Tranca a porta — bloqueia inclusive aberturas por credencial válida até destrancar.</summary>
    [HttpPost("{id:guid}/lock")]
    [Authorize(Roles = "Admin,Operator")]
    public Task<IActionResult> Lock(Guid id, CancellationToken ct) => RunDoorCommand(id, "TrancarPorta", Gateway.LockDoorAsync, ct);

    /// <summary>Destranca a porta (reverte o comando de trancar).</summary>
    [HttpPost("{id:guid}/unlock")]
    [Authorize(Roles = "Admin,Operator")]
    public Task<IActionResult> Unlock(Guid id, CancellationToken ct) => RunDoorCommand(id, "DestrancarPorta", Gateway.UnlockDoorAsync, ct);

    private async Task<IActionResult> RunDoorCommand(Guid id, string action, Func<Domain.Entities.Controller, CancellationToken, Task> command, CancellationToken ct)
    {
        var controller = await Db.Controllers.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (controller is null) return NotFound();

        try
        {
            await command(controller, ct);
            await AuditAsync(controller, action, success: true, error: null, ct);
            return NoContent();
        }
        catch (Exception ex)
        {
            await AuditAsync(controller, action, success: false, error: ex.Message, ct);
            return StatusCode(502, new { error = ex.Message });
        }
    }
}
