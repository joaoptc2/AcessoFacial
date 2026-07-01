using HospitalAccess.Domain.Entities;
using HospitalAccess.Gateway;
using HospitalAccess.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HospitalAccess.Api.Controllers;

public record CreateControllerRequest(
    string Name, string IpAddress, int Port, string SerialNumber,
    string CommunicationPassword, bool SupportsWaitRepeatMessage, int RelayIndex = 0);

public record UpdateControllerRequest(
    string Name, string IpAddress, int Port, string SerialNumber,
    string CommunicationPassword, bool SupportsWaitRepeatMessage, int RelayIndex,
    int TimeoutMs, int RestartCount);

/// <summary>
/// Cadastro dos 30 controladores 8190H, status de sincronização por dispositivo e
/// comando remoto de porta. Cada controlador representa fisicamente uma única porta
/// (o hardware não suporta mais de um relé por controlador) — não existe mais uma
/// entidade "Door" separada.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = "Admin,Operator")]
public class ControllersController : ControllerBase
{
    private readonly AccessDbContext _db;
    private readonly IDeviceGateway _gateway;

    public ControllersController(AccessDbContext db, IDeviceGateway gateway)
    {
        _db = db;
        _gateway = gateway;
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var controllers = await _db.Controllers
            .OrderBy(c => c.Name)
            .Select(c => new
            {
                c.Id, c.Name, c.IpAddress, c.Port, c.SerialNumber, c.SupportsWaitRepeatMessage,
                c.RelayIndex, c.TimeoutMs, c.RestartCount,
                UserCount = c.Permissions.Count,
            })
            .ToListAsync(ct);
        return Ok(controllers);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var controller = await _db.Controllers.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (controller is null) return NotFound();

        return Ok(new
        {
            controller.Id, controller.Name, controller.IpAddress, controller.Port, controller.SerialNumber,
            controller.CommunicationPassword, controller.SupportsWaitRepeatMessage, controller.RelayIndex,
            controller.TimeoutMs, controller.RestartCount,
        });
    }

    /// <summary>Status de sincronização (DeviceSyncStatus) de todos os usuários neste controlador.</summary>
    [HttpGet("{id:guid}/sync-status")]
    public async Task<IActionResult> SyncStatus(Guid id, CancellationToken ct)
    {
        var statuses = await _db.SyncStatuses
            .Where(s => s.ControllerId == id)
            .Include(s => s.User)
            .Select(s => new
            {
                s.UserId, UserName = s.User!.Name, s.State, s.RetryCount, s.LastError, s.UpdatedAt,
            })
            .ToListAsync(ct);
        return Ok(statuses);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateControllerRequest request, CancellationToken ct)
    {
        if (request.SerialNumber.Length != 16)
            return BadRequest("SerialNumber deve ter exatamente 16 dígitos.");

        var controller = new Domain.Entities.Controller
        {
            Name = request.Name,
            IpAddress = request.IpAddress,
            Port = request.Port,
            SerialNumber = request.SerialNumber,
            CommunicationPassword = request.CommunicationPassword,
            SupportsWaitRepeatMessage = request.SupportsWaitRepeatMessage,
            RelayIndex = request.RelayIndex,
        };

        _db.Controllers.Add(controller);
        await _db.SaveChangesAsync(ct);

        return CreatedAtAction(nameof(Get), new { id = controller.Id }, new { controller.Id });
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateControllerRequest request, CancellationToken ct)
    {
        var controller = await _db.Controllers.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (controller is null) return NotFound();
        if (request.SerialNumber.Length != 16)
            return BadRequest("SerialNumber deve ter exatamente 16 dígitos.");

        controller.Name = request.Name;
        controller.IpAddress = request.IpAddress;
        controller.Port = request.Port;
        controller.SerialNumber = request.SerialNumber;
        controller.CommunicationPassword = request.CommunicationPassword;
        controller.SupportsWaitRepeatMessage = request.SupportsWaitRepeatMessage;
        controller.RelayIndex = request.RelayIndex;
        controller.TimeoutMs = request.TimeoutMs;
        controller.RestartCount = request.RestartCount;

        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>Remove o controlador. Falha se ainda houver usuários com permissão nele (remova as permissões antes).</summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var controller = await _db.Controllers.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (controller is null) return NotFound();

        var hasPermissions = await _db.Permissions.AnyAsync(p => p.ControllerId == id, ct);
        if (hasPermissions)
            return Conflict("Existem usuários com permissão neste controlador. Remova as permissões antes de excluir.");

        _db.Controllers.Remove(controller);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>Prova de conceito de conectividade: lê o SN reportado pelo controlador (ReadSN) e confere com o cadastrado.</summary>
    [HttpPost("{id:guid}/test-connection")]
    public async Task<IActionResult> TestConnection(Guid id, CancellationToken ct)
    {
        var controller = await _db.Controllers.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (controller is null) return NotFound();

        try
        {
            var sn = await _gateway.ReadSerialNumberAsync(controller, ct);
            return Ok(new { reportedSerialNumber = sn, matchesRegistered = sn == controller.SerialNumber });
        }
        catch (Exception ex)
        {
            return StatusCode(502, new { error = ex.Message });
        }
    }

    /// <summary>Abre a porta (pulso — volta a fechar após o tempo de liberação configurado no controlador).</summary>
    [HttpPost("{id:guid}/open")]
    [Authorize(Roles = "Admin,Operator,Reception")]
    public Task<IActionResult> Open(Guid id, CancellationToken ct) => RunDoorCommand(id, _gateway.OpenDoorAsync, ct);

    /// <summary>Fecha a porta (encerra o modo "sempre aberto", se ativo).</summary>
    [HttpPost("{id:guid}/close")]
    [Authorize(Roles = "Admin,Operator,Reception")]
    public Task<IActionResult> Close(Guid id, CancellationToken ct) => RunDoorCommand(id, _gateway.CloseDoorAsync, ct);

    /// <summary>Mantém a porta aberta (modo "sempre aberto") até um comando de fechar ou trancar.</summary>
    [HttpPost("{id:guid}/hold-open")]
    [Authorize(Roles = "Admin,Operator")]
    public Task<IActionResult> HoldOpen(Guid id, CancellationToken ct) => RunDoorCommand(id, _gateway.HoldDoorOpenAsync, ct);

    /// <summary>Tranca a porta — bloqueia inclusive aberturas por credencial válida até destrancar.</summary>
    [HttpPost("{id:guid}/lock")]
    [Authorize(Roles = "Admin,Operator")]
    public Task<IActionResult> Lock(Guid id, CancellationToken ct) => RunDoorCommand(id, _gateway.LockDoorAsync, ct);

    /// <summary>Destranca a porta (reverte o comando de trancar).</summary>
    [HttpPost("{id:guid}/unlock")]
    [Authorize(Roles = "Admin,Operator")]
    public Task<IActionResult> Unlock(Guid id, CancellationToken ct) => RunDoorCommand(id, _gateway.UnlockDoorAsync, ct);

    private async Task<IActionResult> RunDoorCommand(Guid id, Func<Domain.Entities.Controller, CancellationToken, Task> command, CancellationToken ct)
    {
        var controller = await _db.Controllers.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (controller is null) return NotFound();

        try
        {
            await command(controller, ct);
            return NoContent();
        }
        catch (Exception ex)
        {
            return StatusCode(502, new { error = ex.Message });
        }
    }
}
