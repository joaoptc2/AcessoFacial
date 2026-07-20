using HospitalAccess.Api.Options;
using HospitalAccess.Api.Services;
using HospitalAccess.Domain.Enums;
using HospitalAccess.Gateway;
using HospitalAccess.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HospitalAccess.Api.Controllers;

/// <summary>Usuário em processamento por um worker neste instante.</summary>
public sealed record SyncProcessingDto(Guid UserId, string UserName, DateTime SinceUtc);

/// <summary>Última varredura do retry automático (prova de vida) + estimativa da próxima.</summary>
public sealed record SyncScanDto(DateTime LastScanAtUtc, DateTime NextScanAtUtc, int ScanIntervalSeconds,
    int LastEnqueued, int Pending, int DueNow, int WaitingBackoff, int Quarantined, DateTime? NextRetryAtUtc);

/// <summary>Totais por categoria (sobre TODAS as pendências, mesmo com a lista limitada).</summary>
public sealed record SyncTotalsDto(int Pending, int DueNow, int WaitingBackoff, int Quarantined, int Conflicts);

public sealed record SyncPendingItemDto(
    Guid UserId, string UserName, UserType UserType, bool UserRevoked,
    Guid ControllerId, string ControllerName, bool ControllerOnline,
    SyncState State, SyncPendingCategory Category, SyncPendingAction Action,
    int RetryCount, string? LastError, DateTime UpdatedAtUtc, DateTime? NextRetryAtUtc,
    uint? ConflictUserCode, bool InQueue, DateTime? ProcessingSinceUtc, DateTime? CircuitOpenUntilUtc);

public sealed record SyncOverviewDto(DateTime GeneratedAtUtc, SyncScanDto? Scan,
    int QueuedUsers, IReadOnlyList<SyncProcessingDto> Processing,
    SyncTotalsDto Totals, int TotalItems, IReadOnlyList<SyncPendingItemDto> Items);

/// <summary>
/// Visão GLOBAL das sincronizações pendentes (usuário × porta), com o estado vivo da fila
/// ("na fila"/"sincronizando agora") e o heartbeat da varredura de retry — responde "o sistema
/// está de fato tentando?". As AÇÕES de forçar continuam nos endpoints existentes
/// (POST /api/users/{id}/resync, POST /api/users/sync-failed, resolução de conflito) — este
/// controller é só leitura, por isso a Recepção também pode ver.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = "Admin,Operator,Reception")]
public class SyncController : ControllerBase
{
    /// <summary>Teto de linhas devolvidas (pós resync-all TUDO vira Pending; a UI avisa o corte).</summary>
    private const int MaxItems = 1000;

    private readonly AccessDbContext _db;
    private readonly IUserSyncQueue _queue;
    private readonly IDeviceGateway _gateway;
    private readonly SyncScanHeartbeat _heartbeat;
    private readonly SyncRetryOptions _retryOptions;

    public SyncController(AccessDbContext db, IUserSyncQueue queue, IDeviceGateway gateway,
        SyncScanHeartbeat heartbeat, IOptions<SyncRetryOptions> retryOptions)
    {
        _db = db;
        _queue = queue;
        _gateway = gateway;
        _heartbeat = heartbeat;
        _retryOptions = retryOptions.Value;
    }

    [HttpGet("pending")]
    public async Task<IActionResult> Pending(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var onlineThreshold = now.AddMinutes(-3); // mesmo limiar do dashboard (/controllers/status)

        // UMA query projetada — nunca materializar o User inteiro (FacePhoto é um byte[] grande).
        // HasPermission vira EXISTS no SQL e distingue envio de remoção (permissão retirada).
        var rows = await _db.SyncStatuses.AsNoTracking()
            .Where(s => s.State == SyncState.Pending || s.State == SyncState.Failed)
            .Select(s => new
            {
                s.UserId,
                UserName = s.User!.Name,
                UserType = s.User!.Type,
                UserRevoked = s.User!.RevokedAtUtc != null,
                s.ControllerId,
                ControllerName = s.Controller!.Name,
                s.Controller!.LastSeenUtc,
                s.State, s.RetryCount, s.LastError, s.UpdatedAt, s.NextRetryAtUtc, s.ConflictUserCode,
                HasPermission = s.User!.Permissions.Any(p => p.ControllerId == s.ControllerId),
            })
            .OrderBy(x => x.ControllerName).ThenBy(x => x.UserName)
            .ToListAsync(ct);

        // Snapshot ÚNICO da fila por request. Corrida benigna: um usuário pode concluir entre a
        // query e este ponto e aparecer "na fila" por um ciclo de poll — aceitável para um painel.
        var active = _queue.ActiveUserIds.ToHashSet();
        var processing = _queue.ProcessingUsers.ToDictionary(kv => kv.Key, kv => kv.Value);
        var circuitByController = rows.Select(r => r.ControllerId).Distinct()
            .ToDictionary(id => id, id => _gateway.GetCircuitOpenUntilUtc(id));

        var items = rows.Select(r => new SyncPendingItemDto(
                r.UserId, r.UserName, r.UserType, r.UserRevoked,
                r.ControllerId, r.ControllerName,
                r.LastSeenUtc != null && r.LastSeenUtc >= onlineThreshold,
                r.State,
                SyncOverviewRules.Categorize(r.State, r.NextRetryAtUtc, r.ConflictUserCode, now),
                SyncOverviewRules.ResolveAction(r.UserRevoked, r.HasPermission),
                r.RetryCount, r.LastError, r.UpdatedAt, r.NextRetryAtUtc, r.ConflictUserCode,
                active.Contains(r.UserId),
                processing.TryGetValue(r.UserId, out var since) ? since : null,
                circuitByController[r.ControllerId]))
            .ToList();

        var totals = new SyncTotalsDto(
            items.Count(i => i.Category == SyncPendingCategory.Pending),
            items.Count(i => i.Category == SyncPendingCategory.DueNow),
            items.Count(i => i.Category == SyncPendingCategory.WaitingBackoff),
            items.Count(i => i.Category == SyncPendingCategory.Quarantined),
            items.Count(i => i.Category == SyncPendingCategory.Conflict));

        var interval = Math.Max(1, _retryOptions.ScanIntervalSeconds);
        var scan = _heartbeat.Last is { } last
            ? new SyncScanDto(last.AtUtc, last.AtUtc.AddSeconds(interval), interval,
                last.Enqueued, last.Pending, last.DueNow, last.WaitingBackoff, last.Quarantined, last.NextRetryAtUtc)
            : null;

        var nameByUser = items.GroupBy(i => i.UserId).ToDictionary(g => g.Key, g => g.First().UserName);
        var processingDtos = processing
            .Select(kv => new SyncProcessingDto(kv.Key,
                nameByUser.TryGetValue(kv.Key, out var name) ? name : "", kv.Value))
            .OrderBy(p => p.SinceUtc)
            .ToList();

        return Ok(new SyncOverviewDto(
            now, scan,
            Math.Max(0, active.Count - processing.Count),
            processingDtos,
            totals, items.Count,
            items.Count > MaxItems ? items.Take(MaxItems).ToList() : items));
    }
}
