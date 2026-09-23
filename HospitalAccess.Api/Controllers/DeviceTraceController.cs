using System.Globalization;
using System.Text;
using HospitalAccess.Api.Services;
using HospitalAccess.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HospitalAccess.Api.Controllers;

/// <summary>
/// Rastreamento de desempenho dos aparelhos: resumo na tela e exportação em CSV.
///
/// O CSV é o produto final — ele sai da máquina e vai ser analisado fora, então precisa se
/// explicar sozinho: nomes de coluna em português, nome e IP da porta junto (não só o GUID) e
/// instantes em ISO-8601 com fuso, para não depender da localidade de quem abrir a planilha.
/// </summary>
[ApiController]
[Route("api/devicetrace")]
[Authorize(Roles = "Admin")]
public class DeviceTraceController : ControllerBase
{
    /// <summary>Teto de linhas por exportação. 3 dias de coleta cabem folgados; o limite existe contra o acidente.</summary>
    private const int MaxRows = 2_000_000;

    private readonly AccessDbContext _db;
    private readonly RuntimeSettingsProvider _settings;
    private readonly DeviceTraceRecorder _recorder;

    public DeviceTraceController(AccessDbContext db, RuntimeSettingsProvider settings, DeviceTraceRecorder recorder)
    {
        _db = db;
        _settings = settings;
        _recorder = recorder;
    }

    /// <summary>
    /// Estado da coleta: se está ligada, quantas linhas já tem, o período coberto e — importante —
    /// quantas amostras foram DESCARTADAS por fila cheia. Sem esse número, alguém analisaria um
    /// CSV com buraco achando que está inteiro.
    /// </summary>
    [HttpGet("summary")]
    public async Task<IActionResult> Summary(CancellationToken ct)
    {
        var total = await _db.DeviceCommandTraces.CountAsync(ct);
        DateTime? first = null, last = null;
        if (total > 0)
        {
            first = await _db.DeviceCommandTraces.MinAsync(t => t.StartedAtUtc, ct);
            last = await _db.DeviceCommandTraces.MaxAsync(t => t.StartedAtUtc, ct);
        }

        // As operações mais custosas POR TEMPO TOTAL (não por média): é onde o ganho mora.
        var porOperacao = await _db.DeviceCommandTraces
            .GroupBy(t => new { t.Channel, t.Operation })
            .Select(g => new
            {
                g.Key.Channel,
                g.Key.Operation,
                Chamadas = g.Count(),
                TempoTotalMs = g.Sum(t => (long)t.DurationMs + t.QueueWaitMs),
                MediaMs = (int)g.Average(t => (double)t.DurationMs),
                EsperaMediaMs = (int)g.Average(t => (double)t.QueueWaitMs),
                Falhas = g.Count(t => t.Outcome != "Success"),
            })
            .OrderByDescending(x => x.TempoTotalMs)
            .Take(15)
            .ToListAsync(ct);

        return Ok(new
        {
            enabled = _settings.Diagnostics.DeviceTraceEnabled,
            retentionDays = _settings.Diagnostics.DeviceTraceRetentionDays,
            total,
            firstAtUtc = first,
            lastAtUtc = last,
            written = _recorder.Written,
            dropped = _recorder.Dropped,
            byOperation = porOperacao,
        });
    }

    /// <summary>Exporta a coleta em CSV. <paramref name="days"/> limita a janela (vazio = tudo).</summary>
    [HttpGet("export.csv")]
    public async Task<IActionResult> ExportCsv([FromQuery] int? days, CancellationToken ct)
    {
        var query = _db.DeviceCommandTraces.AsNoTracking().AsQueryable();
        if (days is > 0)
        {
            var cutoff = DateTime.UtcNow.AddDays(-days.Value);
            query = query.Where(t => t.StartedAtUtc >= cutoff);
        }

        var rows = await query.OrderBy(t => t.StartedAtUtc).Take(MaxRows).ToListAsync(ct);

        var csv = new StringBuilder();
        csv.AppendLine("instante_utc;porta;ip;canal;operacao;origem;espera_fila_ms;duracao_ms;fila_profundidade;" +
                       "desfecho;erro;payload_bytes;timeout_ms;tentativas;codigo_usuario");
        foreach (var r in rows)
        {
            csv.Append(r.StartedAtUtc.ToString("O", CultureInfo.InvariantCulture)).Append(';')
               .Append(Escape(r.ControllerName)).Append(';')
               .Append(Escape(r.ControllerIp)).Append(';')
               .Append(Escape(r.Channel)).Append(';')
               .Append(Escape(r.Operation)).Append(';')
               .Append(Escape(r.Trigger)).Append(';')
               .Append(r.QueueWaitMs).Append(';')
               .Append(r.DurationMs).Append(';')
               .Append(r.QueueDepth).Append(';')
               .Append(Escape(r.Outcome)).Append(';')
               .Append(Escape(r.Error)).Append(';')
               .Append(r.PayloadBytes).Append(';')
               .Append(r.TimeoutMs).Append(';')
               .Append(r.RestartCount).Append(';')
               .Append(r.UserCode?.ToString(CultureInfo.InvariantCulture) ?? string.Empty)
               .Append('\n');
        }

        // BOM: sem ele o Excel abre os acentos errados, e o arquivo existe para ser lido.
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv.ToString())).ToArray();
        var nome = $"desempenho-aparelhos-{DateTime.UtcNow:yyyyMMdd-HHmm}.csv";
        return File(bytes, "text/csv; charset=utf-8", nome);
    }

    public record SetTraceModeRequest(bool Enabled, int? RetentionDays);

    /// <summary>
    /// Liga/desliga a coleta e ajusta o prazo. Endpoint próprio, espelhando /api/devlogs/mode:
    /// a tela de diagnóstico não deveria ter que reenviar TODAS as configurações do sistema só
    /// para virar uma chave.
    /// </summary>
    [HttpPost("mode")]
    public async Task<IActionResult> SetMode([FromBody] SetTraceModeRequest request, CancellationToken ct)
    {
        if (request.RetentionDays is { } dias && dias < 1)
            return BadRequest("O prazo do diagnóstico deve ser de pelo menos 1 dia.");

        var settings = await _db.SystemSettings.FirstOrDefaultAsync(
            s => s.Id == Domain.Entities.SystemSettings.SingletonId, ct);
        if (settings is null)
        {
            settings = new Domain.Entities.SystemSettings();
            _db.SystemSettings.Add(settings);
        }

        settings.DeviceTraceEnabled = request.Enabled;
        if (request.RetentionDays is { } d) settings.DeviceTraceRetentionDays = Math.Max(1, d);
        settings.UpdatedAtUtc = DateTime.UtcNow;
        settings.UpdatedByUsername = User.Identity?.Name;
        await _db.SaveChangesAsync(ct);
        _settings.Invalidate(); // vale na hora, sem reiniciar o serviço

        return Ok(new
        {
            enabled = _settings.Diagnostics.DeviceTraceEnabled,
            retentionDays = _settings.Diagnostics.DeviceTraceRetentionDays,
        });
    }

    /// <summary>Descarta a coleta (para começar uma nova janela limpa).</summary>
    [HttpDelete]
    public async Task<IActionResult> Clear(CancellationToken ct)
    {
        var removed = await _db.DeviceCommandTraces.ExecuteDeleteAsync(ct);
        return Ok(new { removed });
    }

    /// <summary>
    /// Separador é ';' (padrão pt-BR no Excel), então o que é escapado é ele, mais aspas e
    /// quebras de linha — uma mensagem de erro com ponto e vírgula estouraria as colunas.
    /// </summary>
    private static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var needsQuotes = value.Contains(';') || value.Contains('"') || value.Contains('\n') || value.Contains('\r');
        var clean = value.Replace("\r", " ").Replace("\n", " ");
        return needsQuotes ? $"\"{clean.Replace("\"", "\"\"")}\"" : clean;
    }
}
