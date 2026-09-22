using HospitalAccess.Domain.Entities;
using HospitalAccess.Gateway;
using HospitalAccess.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HospitalAccess.Api.Services;

/// <summary>Resultado de definir o horário de um usuário numa porta.</summary>
/// <param name="GroupNumber">Grade ocupada no aparelho, ou null em caso de erro.</param>
/// <param name="Error">Mensagem pronta para o operador quando não deu.</param>
/// <param name="Pushed">
/// O aparelho recebeu a tabela nova? False = o horário está gravado no sistema mas a porta não
/// respondeu; ele vale assim que ela voltar e alguém reaplicar ou ressincronizar.
/// </param>
public sealed record SetScheduleResult(int? GroupNumber, bool Reused, string? Error, bool Pushed = false)
{
    public bool Ok => Error is null;
}

/// <summary>
/// Dono das 64 grades de horário de cada controladora: aloca, libera as que ninguém usa mais e
/// empurra a tabela completa ao aparelho.
///
/// <para>
/// A regra que governa tudo aqui: o comando AddTimeGroup substitui as 64 de uma vez, e grade sem
/// definição chega ao aparelho como SEMPRE FECHADO. Quem escreve essa tabela assume a
/// responsabilidade por ela inteira — por isso a grade reservada é gravada em toda passagem,
/// exista ou não no banco, e é ela que significa "sem restrição".
/// </para>
/// </summary>
public sealed class ControllerTimeGroupService
{
    /// <summary>
    /// Quanto o operador espera pelo aparelho antes de a tela responder. Curto de propósito: a
    /// gravação no sistema já aconteceu, e o que falta é só a confirmação da porta.
    /// </summary>
    public static readonly TimeSpan PushTimeout = TimeSpan.FromSeconds(20);

    private readonly AccessDbContext _db;
    private readonly IDeviceGateway _gateway;
    private readonly ILogger<ControllerTimeGroupService> _logger;

    public ControllerTimeGroupService(AccessDbContext db, IDeviceGateway gateway,
        ILogger<ControllerTimeGroupService> logger)
    {
        _db = db;
        _gateway = gateway;
        _logger = logger;
    }

    // ------------------------------------------------------------------ leitura

    /// <summary>Janelas de uma grade, na forma que a tela usa.</summary>
    public async Task<IReadOnlyList<ScheduleWindow>> GetWindowsAsync(
        Guid controllerId, int groupNumber, CancellationToken ct = default)
    {
        if (groupNumber == TimeGroupAllocation.ReservedUnrestricted)
            return TimeGroupAllocation.UnrestrictedWindows;

        var grade = await _db.ControllerTimeGroups.AsNoTracking()
            .Include(g => g.Segments)
            .FirstOrDefaultAsync(g => g.ControllerId == controllerId && g.GroupNumber == groupNumber, ct);

        if (grade is null) return TimeGroupAllocation.UnrestrictedWindows;

        return [.. grade.Segments.Select(s => new ScheduleWindow(s.Weekday, s.BeginTime, s.EndTime))];
    }

    // ------------------------------------------------------------------ definir horário

    /// <summary>
    /// Define o horário de um usuário numa porta. Aloca (ou reaproveita) a grade, aponta a
    /// permissão para ela, libera o que ficou órfão e reescreve a tabela do aparelho.
    ///
    /// <para>
    /// NÃO enfileira a sincronização do usuário: quem chama decide isso, porque a mesma operação
    /// pode acontecer no meio de uma edição maior e não faz sentido empurrar a pessoa duas vezes.
    /// </para>
    /// </summary>
    public async Task<SetScheduleResult> SetScheduleAsync(
        Guid userId, Guid controllerId, IReadOnlyList<ScheduleWindow> windows, CancellationToken ct = default)
    {
        if (TimeGroupAllocation.Validate(windows) is { } invalido)
            return new SetScheduleResult(null, false, invalido);

        var permissao = await _db.Permissions
            .FirstOrDefaultAsync(p => p.UserId == userId && p.ControllerId == controllerId, ct);
        if (permissao is null)
            return new SetScheduleResult(null, false, "Este usuário não tem permissão nesta porta.");

        var ocupadas = await _db.ControllerTimeGroups.AsNoTracking()
            .Where(g => g.ControllerId == controllerId)
            .ToDictionaryAsync(g => g.GroupNumber, g => g.ContentHash, ct);
        ocupadas[TimeGroupAllocation.ReservedUnrestricted] = "livre";

        var alocacao = TimeGroupAllocation.Allocate(ocupadas, windows);
        if (!alocacao.Ok)
        {
            return new SetScheduleResult(null, false,
                $"Esta porta já usa as {TimeGroupAllocation.MaxGroups} grades de horário do aparelho com "
                + "horários diferentes entre si. Reaproveite um horário já usado por outra pessoa nesta porta "
                + "ou libere alguém que tenha horário exclusivo.");
        }

        var numero = alocacao.GroupNumber!.Value;
        var anterior = permissao.TimeGroup;

        if (!alocacao.Reused && numero != TimeGroupAllocation.ReservedUnrestricted)
        {
            var grade = new ControllerTimeGroup
            {
                ControllerId = controllerId,
                GroupNumber = numero,
                ContentHash = TimeGroupAllocation.Fingerprint(windows),
                Label = DescreverJanelas(windows),
            };
            foreach (var (dia, indice, inicio, fim) in TimeGroupAllocation.WithSegmentIndexes(windows))
            {
                grade.Segments.Add(new ControllerTimeGroupSegment
                {
                    Weekday = dia, SegmentIndex = indice, BeginTime = inicio, EndTime = fim,
                });
            }
            _db.ControllerTimeGroups.Add(grade);
        }

        permissao.TimeGroup = numero;
        await _db.SaveChangesAsync(ct);

        if (anterior != numero) await LiberarOrfasAsync(controllerId, ct);

        // O envio é BEST-EFFORT de propósito: a porta pode estar offline, e perder a configuração
        // por causa disso seria pior que gravá-la e avisar. O banco é a fonte da verdade; a tabela
        // do aparelho é reescrita inteira na próxima passagem bem-sucedida.
        var enviado = await TryPushAsync(controllerId, ct);

        return new SetScheduleResult(numero, alocacao.Reused, null, enviado);
    }

    // ------------------------------------------------------------------ limpeza

    /// <summary>
    /// Devolve ao aparelho as grades que nenhuma permissão usa mais. Sem isto, trocar o horário
    /// de algumas pessoas algumas vezes entope as 64 posições com grades órfãs — e a porta começa
    /// a recusar horários novos sem motivo aparente.
    /// </summary>
    public async Task<int> LiberarOrfasAsync(Guid controllerId, CancellationToken ct = default)
    {
        var emUso = await _db.Permissions
            .Where(p => p.ControllerId == controllerId)
            .Select(p => p.TimeGroup)
            .Distinct()
            .ToListAsync(ct);

        var orfas = await _db.ControllerTimeGroups
            .Where(g => g.ControllerId == controllerId
                        && g.GroupNumber != TimeGroupAllocation.ReservedUnrestricted
                        && !emUso.Contains(g.GroupNumber))
            .ToListAsync(ct);

        if (orfas.Count == 0) return 0;

        _db.ControllerTimeGroups.RemoveRange(orfas);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Grades liberadas na controladora {Controller}: {Numeros}.",
            controllerId, string.Join(", ", orfas.Select(o => o.GroupNumber)));
        return orfas.Count;
    }

    // ------------------------------------------------------------------ envio ao aparelho

    /// <summary>
    /// Tenta reescrever a tabela do aparelho e devolve se conseguiu. Falha de comunicação não é
    /// erro da operação: o horário já está gravado no sistema.
    /// </summary>
    public async Task<bool> TryPushAsync(Guid controllerId, CancellationToken ct = default)
    {
        // Teto de tempo: uma porta offline leva MINUTOS para desistir (a conexão tenta várias
        // vezes antes de falhar), e o operador ficaria olhando a tela travada por causa de um
        // aparelho que nem é o dele. Passado o teto, a gravação vale e o retorno diz que a porta
        // não recebeu.
        using var limite = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limite.CancelAfter(PushTimeout);

        try
        {
            await PushAsync(controllerId, limite.Token);
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(
                "A controladora {Controller} não recebeu a tabela de grades em {Segundos}s. O horário "
                + "está gravado e vale quando a porta voltar.", controllerId, PushTimeout.TotalSeconds);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Horário gravado, mas a controladora {Controller} não recebeu a tabela de grades. "
                + "Ela vale assim que a porta voltar e a configuração for reaplicada ou ressincronizada.",
                controllerId);
            return false;
        }
    }

    /// <summary>
    /// Reescreve as 64 grades da controladora. A grade reservada entra SEMPRE, venha ou não do
    /// banco — é ela que mantém livre quem não tem horário definido.
    /// </summary>
    public async Task PushAsync(Guid controllerId, CancellationToken ct = default)
    {
        var controller = await _db.Controllers.FirstOrDefaultAsync(c => c.Id == controllerId, ct);
        if (controller is null) return;

        await PushAsync(controller, ct);
    }

    /// <inheritdoc cref="PushAsync(Guid, CancellationToken)"/>
    public async Task PushAsync(Controller controller, CancellationToken ct = default)
    {
        var grades = await _db.ControllerTimeGroups.AsNoTracking()
            .Include(g => g.Segments)
            .Where(g => g.ControllerId == controller.Id)
            .ToListAsync(ct);

        var entradas = new List<TimeGroupEntry>
        {
            // A reservada, sempre — mesmo que alguém tenha apagado a linha do banco.
            new(TimeGroupAllocation.ReservedUnrestricted,
                [.. TimeGroupAllocation.WithSegmentIndexes(TimeGroupAllocation.UnrestrictedWindows)
                    .Select(x => new TimeGroupSegmentEntry(x.Weekday, x.SegmentIndex, x.Begin, x.End))]),
        };

        foreach (var g in grades.Where(g => g.GroupNumber != TimeGroupAllocation.ReservedUnrestricted))
        {
            entradas.Add(new TimeGroupEntry(g.GroupNumber,
                [.. g.Segments.Select(s => new TimeGroupSegmentEntry(s.Weekday, s.SegmentIndex, s.BeginTime, s.EndTime))]));
        }

        await _gateway.SyncTimeGroupsAsync(controller, entradas, ct);
        _logger.LogInformation("Grades de horário enviadas ao controlador {Controller}: {Total} definidas.",
            controller.Name, entradas.Count);
    }

    // ------------------------------------------------------------------ apoio

    /// <summary>Rótulo legível para a tela ("Seg–Sex 07:00–19:00"). Não participa da comparação.</summary>
    private static string DescreverJanelas(IReadOnlyList<ScheduleWindow> windows)
    {
        var canonicas = TimeGroupAllocation.Canonicalize(windows);
        if (canonicas.Count == 0) return "Sem restrição";

        var faixas = canonicas
            .Select(w => $"{w.Begin:HH\\:mm}–{w.End:HH\\:mm}")
            .Distinct()
            .ToList();

        var dias = canonicas.Select(w => w.Weekday).Distinct().OrderBy(d => d).ToList();
        string[] nomes = ["Seg", "Ter", "Qua", "Qui", "Sex", "Sáb", "Dom"];
        var descricaoDias = dias.Count == 7
            ? "Todos os dias"
            : string.Join(", ", dias.Select(d => nomes[d]));

        var rotulo = $"{descricaoDias} {string.Join(" / ", faixas)}";
        return rotulo.Length > 120 ? rotulo[..120] : rotulo;
    }
}
