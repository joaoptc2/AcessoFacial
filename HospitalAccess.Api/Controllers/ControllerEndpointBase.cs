using HospitalAccess.Domain.Entities;
using HospitalAccess.Gateway;
using HospitalAccess.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HospitalAccess.Api.Controllers;

/// <summary>
/// Base comum dos endpoints de controlador. A rota <c>api/controllers</c> é atendida por VÁRIOS
/// controllers (portas, rede, relógio, alarmes, quiosque, auditoria de pessoal, fotos de evento)
/// em vez de um arquivo único de ~1000 linhas com sete responsabilidades — as fronteiras já
/// estavam marcadas por comentários de seção; aqui viraram arquivos. Nenhuma rota mudou.
///
/// <para>
/// RBAC: a classe permite as três funções (Admin/Operator/Reception) para que a Recepção possa
/// listar as portas e acionar abrir/fechar. Todo endpoint de escrita/configuração e as leituras
/// sensíveis restringem explicitamente para Admin/Operator — múltiplos <c>[Authorize]</c>
/// combinam por AND, então a Recepção só passa onde não há restrição adicional.
/// </para>
/// </summary>
[ApiController]
[Route("api/controllers")]
[Authorize(Roles = "Admin,Operator,Reception")]
public abstract class ControllerEndpointBase : ControllerBase
{
    protected readonly AccessDbContext Db;
    protected readonly IDeviceGateway Gateway;

    protected ControllerEndpointBase(AccessDbContext db, IDeviceGateway gateway)
    {
        Db = db;
        Gateway = gateway;
    }

    /// <summary>
    /// Lê algo do aparelho e devolve 200, ou 502 com a causa. Falha de comunicação é do
    /// DISPOSITIVO, não da nossa API — daí o 502 em vez de 500.
    /// </summary>
    protected async Task<IActionResult> RunReadAsync<T>(
        Guid id, Func<Domain.Entities.Controller, CancellationToken, Task<T>> read, CancellationToken ct)
    {
        var controller = await Db.Controllers.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (controller is null) return NotFound();

        try
        {
            return Ok(await read(controller, ct));
        }
        catch (Exception ex)
        {
            return StatusCode(502, new { error = ex.Message });
        }
    }

    /// <summary>Escreve algo no aparelho e devolve 204, ou 502 com a causa.</summary>
    protected async Task<IActionResult> RunWriteAsync(
        Guid id, Func<Domain.Entities.Controller, CancellationToken, Task> write, CancellationToken ct)
    {
        var controller = await Db.Controllers.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (controller is null) return NotFound();

        try
        {
            await write(controller, ct);
            return NoContent();
        }
        catch (Exception ex)
        {
            return StatusCode(502, new { error = ex.Message });
        }
    }

    /// <summary>Registra na trilha de auditoria QUEM disparou QUAL comando em QUAL porta e o resultado.</summary>
    protected async Task AuditAsync(Domain.Entities.Controller controller, string action, bool success, string? error, CancellationToken ct)
    {
        Db.ControllerAuditLogs.Add(new ControllerAuditLog
        {
            ControllerId = controller.Id,
            ControllerName = controller.Name,
            Action = action,
            PerformedByUsername = User.Identity?.Name,
            Success = success,
            Error = error,
        });
        await Db.SaveChangesAsync(ct);
    }
}
