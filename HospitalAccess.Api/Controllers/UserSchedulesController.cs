using HospitalAccess.Api.Services;
using HospitalAccess.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HospitalAccess.Api.Controllers;

/// <summary>Uma janela de horário como a tela envia: dia da semana e as duas horas.</summary>
/// <param name="Weekday">0 = segunda-feira ... 6 = domingo.</param>
public record ScheduleWindowDto(byte Weekday, string Begin, string End);

/// <summary>Horário do usuário numa porta.</summary>
public record DoorScheduleDto(
    Guid ControllerId,
    string ControllerName,
    int TimeGroup,
    bool Unrestricted,
    string Label,
    IReadOnlyList<ScheduleWindowDto> Windows);

public record SetDoorScheduleRequest(IReadOnlyList<ScheduleWindowDto> Windows);

/// <summary>
/// Horário de um usuário PORTA A PORTA. Substituiu a grade global: o hardware guarda 64 grades
/// por aparelho, e o sistema as aloca sozinho — quem opera informa o horário, não o número da
/// grade, que é detalhe do protocolo.
///
/// <para>
/// Uma porta sem horário definido fica na grade reservada, que significa SEM RESTRIÇÃO
/// (00:00–23:59, todos os dias). É o padrão de todo usuário novo.
/// </para>
/// </summary>
[ApiController]
[Route("api/users/{userId:guid}/schedules")]
[Authorize(Roles = "Admin,Operator")]
public sealed class UserSchedulesController : ControllerBase
{
    private readonly AccessDbContext _db;
    private readonly ControllerTimeGroupService _schedules;
    private readonly IUserSyncQueue _syncQueue;

    public UserSchedulesController(AccessDbContext db, ControllerTimeGroupService schedules, IUserSyncQueue syncQueue)
    {
        _db = db;
        _schedules = schedules;
        _syncQueue = syncQueue;
    }

    /// <summary>Horário do usuário em cada porta que ele tem permissão.</summary>
    [HttpGet]
    public async Task<IActionResult> List(Guid userId, CancellationToken ct)
    {
        if (!await _db.Users.AnyAsync(u => u.Id == userId, ct)) return NotFound();

        var permissoes = await _db.Permissions.AsNoTracking()
            .Where(p => p.UserId == userId)
            .Select(p => new { p.ControllerId, ControllerName = p.Controller!.Name, p.TimeGroup })
            .OrderBy(p => p.ControllerName)
            .ToListAsync(ct);

        var resultado = new List<DoorScheduleDto>(permissoes.Count);
        foreach (var p in permissoes)
        {
            var janelas = await _schedules.GetWindowsAsync(p.ControllerId, p.TimeGroup, ct);
            var semRestricao = p.TimeGroup == TimeGroupAllocation.ReservedUnrestricted;

            var grade = semRestricao
                ? null
                : await _db.ControllerTimeGroups.AsNoTracking()
                    .FirstOrDefaultAsync(g => g.ControllerId == p.ControllerId && g.GroupNumber == p.TimeGroup, ct);

            resultado.Add(new DoorScheduleDto(
                p.ControllerId,
                p.ControllerName,
                p.TimeGroup,
                semRestricao,
                semRestricao ? "Sem restrição" : grade?.Label ?? "Horário personalizado",
                [.. janelas.Select(w => new ScheduleWindowDto(w.Weekday, w.Begin.ToString("HH\\:mm"), w.End.ToString("HH\\:mm")))]));
        }

        return Ok(resultado);
    }

    /// <summary>
    /// Define o horário do usuário numa porta. Lista vazia devolve a porta à grade reservada
    /// (sem restrição). O número da grade no aparelho é escolhido pelo sistema.
    /// </summary>
    [HttpPut("{controllerId:guid}")]
    public async Task<IActionResult> Set(
        Guid userId, Guid controllerId, [FromBody] SetDoorScheduleRequest request, CancellationToken ct)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return NotFound();

        if (!TryParseWindows(request?.Windows, out var janelas, out var erroFormato))
            return BadRequest(new { error = erroFormato });

        var resultado = await _schedules.SetScheduleAsync(userId, controllerId, janelas, ct);
        if (!resultado.Ok) return BadRequest(new { error = resultado.Error });

        // A pessoa precisa ser reenviada: o aparelho guarda o número da grade DENTRO do cadastro
        // dela. Reescrever a tabela de grades não muda para qual delas a pessoa aponta.
        _syncQueue.EnqueueSync(userId);

        return Ok(new { timeGroup = resultado.GroupNumber, reused = resultado.Reused, pushed = resultado.Pushed });
    }

    /// <summary>Converte as horas em texto ("07:00") e devolve a primeira que não fizer sentido.</summary>
    private static bool TryParseWindows(
        IReadOnlyList<ScheduleWindowDto>? entrada, out IReadOnlyList<ScheduleWindow> janelas, out string? erro)
    {
        janelas = [];
        erro = null;
        if (entrada is null || entrada.Count == 0) return true; // vazio = sem restrição

        var lista = new List<ScheduleWindow>(entrada.Count);
        foreach (var w in entrada)
        {
            if (!TimeOnly.TryParse(w.Begin, out var inicio) || !TimeOnly.TryParse(w.End, out var fim))
            {
                erro = $"Horário inválido: '{w.Begin}' – '{w.End}'. Use o formato HH:MM.";
                return false;
            }
            lista.Add(new ScheduleWindow(w.Weekday, inicio, fim));
        }

        janelas = lista;
        return true;
    }
}
