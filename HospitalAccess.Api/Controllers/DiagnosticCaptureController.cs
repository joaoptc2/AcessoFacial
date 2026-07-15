using System.IO.Compression;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HospitalAccess.Api.Controllers;

/// <summary>
/// Captura de diagnóstico para descobrir o que um aparelho envia por HTTP (phone-home) quando o
/// formato/caminho ainda é desconhecido — ex.: firmware novo com "Server Response Error". Registra
/// método, caminho, cabeçalhos e corpo (com gunzip) de qualquer POST/PUT que NÃO casou com uma rota
/// real, e responde um ack permissivo. É uma rota catch-all (menor prioridade), então nunca ofusca
/// os endpoints reais nem o fallback do SPA (que é GET).
///
/// DESLIGADO por padrão: só age quando <c>Diagnostics:CaptureDeviceRequests=true</c> (senão devolve
/// 404 normal). Ligue temporariamente para investigar, capture o log e desligue — o corpo pode
/// conter dados pessoais.
/// </summary>
[ApiController]
[AllowAnonymous]
public sealed class DiagnosticCaptureController : ControllerBase
{
    private const int MaxBodyLog = 8192;

    private readonly IConfiguration _config;
    private readonly ILogger<DiagnosticCaptureController> _logger;

    public DiagnosticCaptureController(IConfiguration config, ILogger<DiagnosticCaptureController> logger)
    {
        _config = config;
        _logger = logger;
    }

    [HttpPost("{**path}")]
    [HttpPut("{**path}")]
    public async Task<IActionResult> Capture(CancellationToken ct)
    {
        if (!_config.GetValue<bool>("Diagnostics:CaptureDeviceRequests"))
            return NotFound(); // comportamento normal quando a captura está desligada

        using var ms = new MemoryStream();
        await Request.Body.CopyToAsync(ms, ct);
        var bytes = ms.ToArray();

        var isGzip = bytes.Length >= 2 && bytes[0] == 0x1F && bytes[1] == 0x8B;
        string body;
        try { body = Encoding.UTF8.GetString(isGzip ? Decompress(bytes) : bytes); }
        catch { body = $"<{bytes.Length} bytes binários>"; }
        if (body.Length > MaxBodyLog) body = body[..MaxBodyLog] + "...(truncado)";

        var headers = string.Join(" | ", Request.Headers.Select(h => $"{h.Key}: {h.Value}"));

        _logger.LogWarning(
            "[CAPTURA-DEVICE] {Method} {Path}{Query}\n  Content-Type: {CT}  (len={Len}, gzip={Gzip})\n  HEADERS: {Headers}\n  BODY: {Body}",
            Request.Method, Request.Path, Request.QueryString, Request.ContentType ?? "(nenhum)", bytes.Length, isGzip, headers, body);

        // Ack permissivo cobrindo os estilos comuns (OneCard: code=200 / AI Series: success=0).
        return Ok(new { code = 200, success = 0, msg = "ok" });
    }

    private static byte[] Decompress(byte[] gz)
    {
        using var input = new MemoryStream(gz);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray();
    }
}
