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
/// Leitura reversa: compara quem o APARELHO diz ter cadastrado com quem o banco diz que deveria
/// estar. É a conferência de que a realidade bate com o cadastro — e o caminho para reparar a
/// divergência. Inclui o resincronizar forçado (apaga tudo do aparelho e reenvia).
/// </summary>
public sealed class ControllerPersonnelAuditController : ControllerEndpointBase
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SingleFlight _singleFlight;
    private readonly IUserSyncQueue _syncQueue;
    private readonly PersonnelAuditState _auditState;
    private readonly ILogger<ControllerPersonnelAuditController> _logger;

    public ControllerPersonnelAuditController(AccessDbContext db, IDeviceGateway gateway,
        IServiceScopeFactory scopeFactory, SingleFlight singleFlight, IUserSyncQueue syncQueue,
        PersonnelAuditState auditState, ILogger<ControllerPersonnelAuditController> logger)
        : base(db, gateway)
    {
        _scopeFactory = scopeFactory;
        _singleFlight = singleFlight;
        _syncQueue = syncQueue;
        _auditState = auditState;
        _logger = logger;
    }


    private sealed record AuditReport(
        List<AuditMissingUser> MissingOnDevice, List<uint> ExtraOnDevice, int DeviceCount, int ExpectedCount);

    /// <summary>
    /// Núcleo da auditoria (compartilhado entre a auditoria individual e a de todos os
    /// controladores): compara os códigos lidos do aparelho com os usuários ATIVOS que têm
    /// permissão nesta porta. Revogado/expirado NÃO deve estar no aparelho: a permissão dele
    /// permanece no banco (para reativação), e contá-la aqui fazia um visitante revogado com
    /// sucesso aparecer como "Faltando no dispositivo" para sempre — e um revogado que FICOU
    /// no aparelho passava despercebido (ele aparece como "Extra", que é o estado verdadeiro).
    /// </summary>
    private static async Task<AuditReport> BuildAuditReportAsync(
        AccessDbContext db, Guid controllerId, HashSet<uint> onDevice, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var expected = await db.Permissions.AsNoTracking()
            .Where(p => p.ControllerId == controllerId
                        && p.User!.RevokedAtUtc == null
                        && (p.User.ValidUntil == null || p.User.ValidUntil > now))
            .Select(p => new { p.UserId, p.User!.UserCode, p.User.Name, p.User.Type })
            .ToListAsync(ct);

        var expectedCodes = expected.Select(e => e.UserCode).ToHashSet();
        var missing = expected
            .Where(e => !onDevice.Contains(e.UserCode))
            .GroupBy(e => e.UserCode).Select(g => g.First())
            .OrderBy(e => e.Name)
            .Select(e => new AuditMissingUser(e.UserId, e.UserCode, e.Name, e.Type.ToString()))
            .ToList();

        return new AuditReport(
            missing,
            onDevice.Except(expectedCodes).OrderBy(c => c).ToList(),
            onDevice.Count,
            expectedCodes.Count);
    }

    /// <summary>Compara os usuários efetivamente cadastrados no controlador com as permissões no banco.</summary>
    [HttpGet("{id:guid}/personnel-audit")]
    [Authorize(Roles = "Admin,Operator")]
    public async Task<IActionResult> PersonnelAudit(Guid id, CancellationToken ct)
    {
        var controller = await Db.Controllers.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (controller is null) return NotFound();

        try
        {
            var onDevice = (await Gateway.ReadRegisteredUserCodesAsync(controller, ct)).ToHashSet();
            return Ok(await BuildAuditReportAsync(Db, id, onDevice, ct));
        }
        catch (Exception ex)
        {
            return StatusCode(502, new { error = ex.Message });
        }
    }

    /// <summary>
    /// DISPARA a auditoria de usuários em TODOS os controladores e responde na hora (202).
    ///
    /// <para>
    /// Antes isto era um GET que fazia a varredura DENTRO da requisição. Como a leitura de cada
    /// aparelho tem teto de 3 minutos e a concorrência é 4, o pior caso com vários controladores
    /// lentos passa de 20 minutos — muito além do <c>proxy_read_timeout</c> do Nginx (60 s por
    /// padrão). A tela quebrava justamente quando havia aparelho com problema, que é quando a
    /// auditoria importa. Agora a varredura roda em segundo plano e a tela consulta o resultado
    /// pelo GET — mesmo padrão do resync-all.
    /// </para>
    /// </summary>
    [HttpPost("personnel-audit-all")]
    [Authorize(Roles = "Admin,Operator")]
    public IActionResult StartPersonnelAuditAll()
    {
        const string flightKey = "personnel-audit-all";
        if (!_singleFlight.TryBegin(flightKey))
            return Conflict(new { error = "Já existe uma auditoria em andamento." });

        _ = Task.Run(async () =>
        {
            // Escopo próprio: a requisição que disparou já terminou e levou o DbContext dela.
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AccessDbContext>();
            var gateway = scope.ServiceProvider.GetRequiredService<IDeviceGateway>();
            var state = scope.ServiceProvider.GetRequiredService<PersonnelAuditState>();

            try
            {
                await RunPersonnelAuditAsync(db, gateway, state, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha na auditoria de pessoal de todos os controladores.");
                state.MarkFailed(ex.Message);
            }
            finally
            {
                _singleFlight.End(flightKey);
            }
        });

        return Accepted(new { message = "Auditoria iniciada. Acompanhe pelo estado da tela." });
    }

    /// <summary>
    /// Estado e resultado da última auditoria de todos os controladores. Enquanto <c>phase</c> é
    /// <c>Running</c>, <c>result</c> traz a auditoria ANTERIOR (a tela mostra o que já sabe em vez
    /// de piscar vazia) e <c>done</c>/<c>total</c> dão o progresso.
    /// </summary>
    [HttpGet("personnel-audit-all")]
    [Authorize(Roles = "Admin,Operator")]
    public IActionResult PersonnelAuditAll() => Ok(_auditState.Snapshot());

    /// <summary>
    /// Núcleo da varredura. Falha de um aparelho não derruba os demais: o item correspondente sai
    /// com <c>error</c> preenchido.
    /// </summary>
    private static async Task RunPersonnelAuditAsync(
        AccessDbContext db, IDeviceGateway gateway, PersonnelAuditState state, CancellationToken ct)
    {
        var controllers = await db.Controllers.AsNoTracking().OrderBy(c => c.Name).ToListAsync(ct);
        state.MarkRunning(controllers.Count);

        // Só as LEITURAS DE DISPOSITIVO rodam em paralelo (teto de 4 — ~30 aparelhos numa rede
        // sensível a rajadas). O cálculo do "esperado" usa o DbContext, que NÃO é thread-safe,
        // então roda sequencial depois que todas as leituras terminam.
        using var gate = new SemaphoreSlim(4);
        var reads = await Task.WhenAll(controllers.Select(async controller =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var codes = (await gateway.ReadRegisteredUserCodesAsync(controller, ct)).ToHashSet();
                return (Controller: controller, Codes: (HashSet<uint>?)codes, Error: (string?)null);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return (Controller: controller, Codes: null, Error: ex.Message);
            }
            finally
            {
                gate.Release();
                state.ReportProgress();
            }
        }));

        var results = new List<PersonnelAuditItem>(reads.Length);
        foreach (var read in reads)
        {
            if (read.Codes is null)
            {
                results.Add(new PersonnelAuditItem(
                    read.Controller.Id, read.Controller.Name, read.Error, null, null, null, null));
                continue;
            }

            var report = await BuildAuditReportAsync(db, read.Controller.Id, read.Codes, ct);
            results.Add(new PersonnelAuditItem(
                read.Controller.Id, read.Controller.Name, null,
                report.MissingOnDevice, report.ExtraOnDevice, report.DeviceCount, report.ExpectedCount));
        }

        state.MarkCompleted(new PersonnelAuditReport(DateTime.UtcNow, results));
    }

    /// <summary>
    /// REPARA a divergência detectada pela auditoria: usuários ativos com permissão nesta porta
    /// cujo status diz 'Synced' mas que estão AUSENTES no aparelho voltam a 'Pending' (com
    /// retry/quarentena zerados) e são reenfileirados na fila de sincronização. Divergência
    /// típica herdada da era do "sucesso silencioso" (escrita marcada Synced sem o aparelho ter
    /// gravado). Usuários EXTRAS no aparelho são apenas reportados — a remoção segue manual.
    /// </summary>
    [HttpPost("{id:guid}/personnel-audit/repair")]
    [Authorize(Roles = "Admin,Operator")]
    public async Task<IActionResult> RepairPersonnelAudit(Guid id, CancellationToken ct)
    {
        var controller = await Db.Controllers.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (controller is null) return NotFound();

        try
        {
            var onDevice = (await Gateway.ReadRegisteredUserCodesAsync(controller, ct)).ToHashSet();

            // Candidatos: permissão nesta porta + usuário ativo + status Synced (o banco acha
            // que está lá) + AUSENTE no aparelho. Visitante expirado fica de fora (não recadastrar).
            var now = DateTime.UtcNow;
            var candidates = await Db.SyncStatuses
                .Where(s => s.ControllerId == id
                            && s.State == SyncState.Synced
                            && s.User!.RevokedAtUtc == null
                            && (s.User.ValidUntil == null || s.User.ValidUntil > now)
                            && Db.Permissions.Any(p => p.ControllerId == id && p.UserId == s.UserId))
                .Include(s => s.User)
                .ToListAsync(ct);

            var repairedUserIds = new List<Guid>();
            foreach (var status in candidates)
            {
                if (onDevice.Contains(status.User!.UserCode)) continue; // realmente está no aparelho

                status.State = SyncState.Pending;
                status.RetryCount = 0;
                status.NextRetryAtUtc = null;
                status.LastError = null;
                status.UpdatedAt = DateTime.UtcNow;
                repairedUserIds.Add(status.UserId);
            }
            await Db.SaveChangesAsync(ct);

            var enqueued = _syncQueue.EnqueueMany(repairedUserIds.Distinct());
            await AuditAsync(controller, $"RepararAuditoria#{repairedUserIds.Count}", success: true, error: null, ct);
            _logger.LogInformation(
                "Reparo de auditoria no controlador {Controller}: {Count} usuário(s) marcados para re-envio ({Enqueued} enfileirados).",
                controller.Name, repairedUserIds.Count, enqueued);

            return Ok(new { repaired = repairedUserIds.Count, enqueued });
        }
        catch (Exception ex)
        {
            await AuditAsync(controller, "RepararAuditoria", success: false, error: ex.Message, ct);
            return StatusCode(502, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Reenvia UM usuário apontado como "Faltando no dispositivo" pela auditoria. O resync
    /// comum não serve aqui: ele só reseta linhas Failed, e a divergência típica é uma linha
    /// SYNCED com o usuário ausente do aparelho — o SyncUserAsync a pularia como "já ok".
    /// Este endpoint volta a linha desta porta para Pending (qualquer estado, exceto conflito
    /// de duplicidade, que tem fluxo próprio) e enfileira; sem linha, só enfileira (a
    /// sincronização cria o status ao enviar).
    /// </summary>
    [HttpPost("{id:guid}/personnel-audit/repair/{userId:guid}")]
    [Authorize(Roles = "Admin,Operator")]
    public async Task<IActionResult> RepairPersonnelAuditUser(Guid id, Guid userId, CancellationToken ct)
    {
        var controller = await Db.Controllers.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (controller is null) return NotFound();
        var user = await Db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return NotFound(new { error = "Usuário não encontrado." });

        var now = DateTime.UtcNow;
        if (user.RevokedAtUtc != null || (user.ValidUntil != null && user.ValidUntil <= now))
            return Conflict(new { error = "Usuário revogado/expirado não é reenviado ao aparelho." });
        if (!await Db.Permissions.AnyAsync(p => p.ControllerId == id && p.UserId == userId, ct))
            return Conflict(new { error = "O usuário não tem permissão nesta porta." });

        var status = await Db.SyncStatuses
            .FirstOrDefaultAsync(s => s.ControllerId == id && s.UserId == userId, ct);
        if (status is not null)
        {
            if (status.ConflictUserCode != null)
                return Conflict(new { error = "Há um conflito de face duplicada nesta porta — resolva pelo fluxo de conflito (substituir/manter)." });
            status.State = SyncState.Pending;
            status.RetryCount = 0;
            status.NextRetryAtUtc = null;
            status.LastError = null;
            status.UpdatedAt = now;
            await Db.SaveChangesAsync(ct);
        }

        _syncQueue.EnqueueSync(userId);
        await AuditAsync(controller, $"ReenviarUsuário#{user.UserCode}", success: true, error: null, ct);
        return Accepted(new { message = "Reenvio enfileirado." });
    }

    /// <summary>
    /// Exclui uma pessoa específica (por código) diretamente do controlador — usado na auditoria,
    /// para remover cadastros indevidos (ExtraOnDevice), e na resolução de face duplicada. Auditado.
    /// </summary>
    [HttpDelete("{id:guid}/persons/{userCode:long}")]
    [Authorize(Roles = "Admin,Operator")]
    public async Task<IActionResult> DeletePersonFromDevice(Guid id, long userCode, CancellationToken ct)
    {
        var controller = await Db.Controllers.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (controller is null) return NotFound();
        try
        {
            await Gateway.DeletePersonAsync(controller, (uint)userCode, ct);
            await AuditAsync(controller, $"ExcluirPessoa#{userCode}", success: true, error: null, ct);
            return NoContent();
        }
        catch (Exception ex)
        {
            await AuditAsync(controller, $"ExcluirPessoa#{userCode}", success: false, error: ex.Message, ct);
            return StatusCode(502, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Resincronização FORÇADA: apaga TODAS as pessoas do controlador e reenvia os usuários do
    /// sistema com permissão nele. Roda em segundo plano (pode demorar). Auditado.
    /// </summary>
    [HttpPost("{id:guid}/resync-all")]
    [Authorize(Roles = "Admin,Operator")]
    public async Task<IActionResult> ResyncAll(Guid id, CancellationToken ct)
    {
        var controller = await Db.Controllers.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (controller is null) return NotFound();

        // Single-flight por controlador: cliques repetidos empilhavam ClearAllPersons +
        // re-upload COMPLETO concorrentes no mesmo aparelho — a operação mais cara que existe.
        var flightKey = $"resync:{id}";
        if (!_singleFlight.TryBegin(flightKey))
            return Conflict(new { error = "Já existe uma resincronização em andamento neste controlador." });

        try
        {
            await AuditAsync(controller, "ResincronizarForçado", success: true, error: null, ct);
        }
        catch
        {
            // Falha ANTES de agendar o job: liberar a chave, senão o endpoint fica preso em 409.
            _singleFlight.End(flightKey);
            throw;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var sync = scope.ServiceProvider.GetRequiredService<IUserSyncService>();
                await sync.ForceResyncControllerAsync(id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha no resync forçado do controlador {ControllerId}.", id);
            }
            finally
            {
                _singleFlight.End(flightKey);
            }
        });

        return Accepted(new { message = "Resincronização forçada iniciada." });
    }
}
