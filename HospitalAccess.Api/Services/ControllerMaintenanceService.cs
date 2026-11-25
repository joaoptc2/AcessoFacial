using HospitalAccess.Domain.Entities;
using HospitalAccess.Domain.Enums;
using HospitalAccess.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace HospitalAccess.Api.Services;

/// <summary>Estado de manutenção depois de uma mudança, para a tela.</summary>
/// <param name="Requeued">Usuários reenfileirados para esta porta na saída da manutenção.</param>
/// <param name="TimeGroupsPushed">
/// Na saída: se a porta recebeu a tabela de grades de horário (null na entrada). False = ela ainda
/// não responde; as grades precisam ser reaplicadas quando ela voltar de fato.
/// </param>
public sealed record MaintenanceState(
    bool InMaintenance, DateTime? SinceUtc, string? Note, int Requeued = 0, bool? TimeGroupsPushed = null);

/// <summary>
/// Coloca e tira uma porta de manutenção. Em manutenção, o sistema para de mandar trabalho
/// AUTOMÁTICO a ela — monitoramento, coleta de registros, cadastros e remoções ficam em espera,
/// sem tocar o aparelho. Ao sair, devolve tudo à fila na ordem certa: primeiro as grades de
/// horário (os cadastros apontam para elas), depois os cadastros e remoções pendentes.
///
/// <para>
/// Medido em nov/2026: o Quarto 9, morto por semanas, consumia 20% de todo o tempo de comunicação
/// só para o sistema redescobrir, a cada ciclo, que ele não respondia — e chegou a prender 6 dos 8
/// workers da sincronização esperando por ele.
/// </para>
/// </summary>
public sealed class ControllerMaintenanceService
{
    public const int MaxNoteLength = 200;

    private readonly AccessDbContext _db;
    private readonly IUserSyncQueue _queue;
    private readonly ControllerTimeGroupService _timeGroups;
    private readonly ILogger<ControllerMaintenanceService> _logger;

    public ControllerMaintenanceService(AccessDbContext db, IUserSyncQueue queue,
        ControllerTimeGroupService timeGroups, ILogger<ControllerMaintenanceService> logger)
    {
        _db = db;
        _queue = queue;
        _timeGroups = timeGroups;
        _logger = logger;
    }

    /// <summary>
    /// Coloca em manutenção (ou só atualiza o motivo, se já estiver — a data de início é mantida).
    /// Null = controlador inexistente.
    /// </summary>
    public async Task<MaintenanceState?> EnterAsync(Guid controllerId, string? note, string? username, CancellationToken ct = default)
    {
        var controller = await _db.Controllers.AsNoTracking().FirstOrDefaultAsync(c => c.Id == controllerId, ct);
        if (controller is null) return null;

        var motivo = Normalize(note);
        var desde = controller.MaintenanceSinceUtc ?? DateTime.UtcNow;

        // ExecuteUpdate, e não SaveChanges na entidade: o health-check grava LastSeenUtc nesta
        // mesma linha a cada ciclo, e o token de concorrência (xmin) faria um SaveChanges colidir.
        await _db.Controllers.Where(c => c.Id == controllerId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.MaintenanceSinceUtc, desde)
                .SetProperty(c => c.MaintenanceNote, motivo), ct);

        if (controller.MaintenanceSinceUtc is null)
        {
            await AuditAsync(controller, motivo is null ? "EntrarEmManutencao" : $"EntrarEmManutencao: {motivo}", username, ct);
            _logger.LogWarning("Controlador {Controller} ({Ip}) colocado em manutenção por {User}{Motivo}.",
                controller.Name, controller.IpAddress, username ?? "sistema", motivo is null ? "" : $" — {motivo}");
        }

        return new MaintenanceState(true, desde, motivo);
    }

    /// <summary>
    /// Tira de manutenção e devolve à fila o que ficou esperando. Null = controlador inexistente.
    /// Leva até <see cref="ControllerTimeGroupService.PushTimeout"/> se a porta ainda não responder
    /// (o envio das grades desiste no teto e a resposta diz que elas não chegaram).
    /// </summary>
    public async Task<MaintenanceState?> LeaveAsync(Guid controllerId, string? username, CancellationToken ct = default)
    {
        var controller = await _db.Controllers.AsNoTracking().FirstOrDefaultAsync(c => c.Id == controllerId, ct);
        if (controller is null) return null;
        if (controller.MaintenanceSinceUtc is null) return new MaintenanceState(false, null, null);

        await _db.Controllers.Where(c => c.Id == controllerId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.MaintenanceSinceUtc, (DateTime?)null)
                .SetProperty(c => c.MaintenanceNote, (string?)null), ct);
        await AuditAsync(controller, "SairDaManutencao", username, ct);

        // 1) Grades primeiro: os cadastros que vêm a seguir apontam para elas, e uma grade
        //    alterada durante a manutenção não chegou ao aparelho.
        var gradesEnviadas = await _timeGroups.TryPushAsync(controllerId, ct);

        // 2) Tudo o que ficou esperando nesta porta vence AGORA (cadastros e remoções). Falhas de
        //    antes da manutenção entram junto: a porta acabou de voltar, é a hora de tentar.
        var agora = DateTime.UtcNow;
        await _db.SyncStatuses
            .Where(s => s.ControllerId == controllerId && s.State == SyncState.Failed
                        && s.NextRetryAtUtc != null && s.NextRetryAtUtc > agora)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.NextRetryAtUtc, agora), ct);

        var usuarios = await _db.SyncStatuses.AsNoTracking()
            .Where(s => s.ControllerId == controllerId
                        && (s.State == SyncState.Pending || (s.State == SyncState.Failed && s.NextRetryAtUtc != null)))
            .Select(s => s.UserId)
            .Distinct()
            .ToListAsync(ct);
        var reenfileirados = _queue.EnqueueMany(usuarios);

        _logger.LogWarning(
            "Controlador {Controller} ({Ip}) saiu da manutenção por {User}: {Users} usuário(s) reenfileirado(s); grades {Grades}.",
            controller.Name, controller.IpAddress, username ?? "sistema", reenfileirados,
            gradesEnviadas ? "enviadas" : "NÃO enviadas (porta sem resposta)");

        return new MaintenanceState(false, null, null, reenfileirados, gradesEnviadas);
    }

    private async Task AuditAsync(Controller controller, string action, string? username, CancellationToken ct)
    {
        _db.ControllerAuditLogs.Add(new ControllerAuditLog
        {
            ControllerId = controller.Id,
            ControllerName = controller.Name,
            Action = action,
            PerformedByUsername = username,
            Success = true,
        });
        await _db.SaveChangesAsync(ct);
    }

    private static string? Normalize(string? note)
    {
        var trimmed = note?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        return trimmed.Length > MaxNoteLength ? trimmed[..MaxNoteLength] : trimmed;
    }
}
