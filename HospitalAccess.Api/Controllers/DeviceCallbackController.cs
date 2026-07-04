using System.IO.Compression;
using System.Text;
using System.Text.Json;
using HospitalAccess.Domain.Entities;
using HospitalAccess.Domain.Enums;
using HospitalAccess.Infrastructure.Devices;
using HospitalAccess.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;

namespace HospitalAccess.Api.Controllers;

/// <summary>
/// Endpoints "phone-home": o CONTROLADOR é o cliente HTTP e empurra eventos para o servidor.
/// Portado do sistema de referência (protocolo AI Series). Este é um canal ALTERNATIVO ao push do
/// SDK binário (BeginWatch) — útil quando o firmware está configurado para discar HTTP.
///
/// SEGURANÇA: os aparelhos AI Series NÃO conseguem enviar cabeçalhos HTTP customizados, então não dá
/// para autenticar por token aqui. Este endpoint é <c>[AllowAnonymous]</c> e deve ser protegido por
/// REDE (VLAN/firewall) — exponha-o só à sub-rede dos controladores.
/// </summary>
[ApiController]
[AllowAnonymous]
public sealed class DeviceCallbackController : ControllerBase
{
    private readonly AccessDbContext _db;
    private readonly TimeZoneInfo _deviceTimeZone;
    private readonly ILogger<DeviceCallbackController> _logger;

    public DeviceCallbackController(AccessDbContext db, TimeZoneInfo deviceTimeZone, ILogger<DeviceCallbackController> logger)
    {
        _db = db;
        _deviceTimeZone = deviceTimeZone;
        _logger = logger;
    }

    /// <summary>
    /// <c>POST /note/insertNoteFace</c> — evento de acesso em tempo real. O aparelho envia
    /// multipart com <c>recordJson</c> (JSON, frequentemente GZIP mesmo declarando outro Content-Type)
    /// e um <c>pic</c> JPEG opcional. Responde <c>{"success":0,"msg":"OK"}</c> (success=0 = OK; qualquer
    /// outro valor faz o aparelho reenviar).
    /// </summary>
    [HttpPost("/note/insertNoteFace")]
    public async Task<IActionResult> InsertNoteFace(CancellationToken ct)
    {
        byte[]? recordBytes = await ReadRecordJsonAsync(ct);
        if (recordBytes is null || recordBytes.Length == 0)
            return BadRequest("recordJson ausente/vazio");

        // O firmware manda gzip mesmo declarando application/json — detectar magic 1F 8B.
        var raw = IsGzip(recordBytes) ? Decompress(recordBytes) : recordBytes;

        JsonDocument doc;
        try { doc = JsonDocument.Parse(raw); }
        catch (JsonException ex)
        {
            _logger.LogWarning("[insertNoteFace] JSON inválido: {Msg}", ex.Message);
            return BadRequest("recordJson não é JSON válido");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return BadRequest("recordJson deve ser objeto");

            var deviceKey = GetString(root, "deviceId", "deviceKey");
            if (string.IsNullOrWhiteSpace(deviceKey))
            {
                // Sem deviceKey não dá para associar a um controlador — ack e ignora (não criar fantasma).
                _logger.LogWarning("[insertNoteFace] evento sem deviceKey — ignorado");
                return Ok(new ProtocolOk());
            }

            var controller = await _db.Controllers.FirstOrDefaultAsync(c => c.SerialNumber == deviceKey, ct);
            if (controller is null)
            {
                _logger.LogWarning("[insertNoteFace] deviceKey {Key} não corresponde a nenhum controlador — ignorado", deviceKey);
                return Ok(new ProtocolOk());
            }

            var employeeNo = GetString(root, "employeeId", "employeeNoString");
            var employeeName = GetString(root, "employeeName");
            var notePass = GetInt(root, "notePass") ?? 0;
            var noteTimeStr = GetString(root, "noteTime");

            controller.LastSeenUtc = DateTime.UtcNow;

            var log = new AccessLog
            {
                TimestampUtc = ParseDeviceTime(noteTimeStr),
                UserCode = uint.TryParse(employeeNo, out var uc) ? uc : null,
                UserName = string.IsNullOrEmpty(employeeName) ? null : employeeName,
                ControllerId = controller.Id,
                ControllerName = controller.Name,
                ControllerSerialNumber = controller.SerialNumber,
                RecordSerialNumber = null, // o push HTTP não traz nº de série do registro
                Method = AccessMethod.Face,
                RawEventCode = notePass,
                // notePass: 1 = liberado (0 = negado) — NÃO é contíguo (ver DeviceErrorCodes.AccessResult).
                Granted = notePass == 1,
            };
            _db.AccessLogs.Add(log);
            await _db.SaveChangesAsync(ct);

            _logger.LogInformation("[insertNoteFace] {Controller} user={User} pass={Pass} ({Desc})",
                controller.Name, employeeNo, notePass, DeviceErrorCodes.DescribeAccess(notePass));
        }

        return Ok(new ProtocolOk());
    }

    // ------------------------------------------------------------------ //
    // Helpers
    // ------------------------------------------------------------------ //

    /// <summary>Lê o campo <c>recordJson</c> como bytes crus (preserva gzip), seja multipart ou corpo direto.</summary>
    private async Task<byte[]?> ReadRecordJsonAsync(CancellationToken ct)
    {
        var contentType = Request.ContentType ?? "";
        if (contentType.Contains("multipart/", StringComparison.OrdinalIgnoreCase))
        {
            var boundary = HeaderUtilities.RemoveQuotes(
                MediaTypeHeaderValue.Parse(contentType).Boundary).Value;
            if (string.IsNullOrEmpty(boundary)) return null;

            var reader = new MultipartReader(boundary, Request.Body);
            MultipartSection? section;
            while ((section = await reader.ReadNextSectionAsync(ct)) is not null)
            {
                if (section.ContentDisposition is null) continue;
                if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var cd)) continue;
                var name = cd.Name.Value?.Trim('"');
                if (!string.Equals(name, "recordJson", StringComparison.OrdinalIgnoreCase))
                    continue; // ignoramos 'pic' por ora (foto de evento vem pelo caminho do SDK)
                using var ms = new MemoryStream();
                await section.Body.CopyToAsync(ms, ct);
                return ms.ToArray();
            }
            return null;
        }

        // Sem multipart: alguns firmwares mandam o gzip/JSON direto no corpo.
        using var body = new MemoryStream();
        await Request.Body.CopyToAsync(body, ct);
        return body.ToArray();
    }

    private static bool IsGzip(byte[] b) => b.Length >= 2 && b[0] == 0x1F && b[1] == 0x8B;

    private static byte[] Decompress(byte[] gz)
    {
        using var input = new MemoryStream(gz);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray();
    }

    /// <summary>noteTime é o relógio LOCAL do aparelho ("yyyy-MM-dd HH:mm:ss") — convertemos para UTC.</summary>
    private DateTime ParseDeviceTime(string? s)
    {
        if (!string.IsNullOrWhiteSpace(s) &&
            DateTime.TryParseExact(s, "yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var local))
        {
            var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
            return TimeZoneInfo.ConvertTimeToUtc(unspecified, _deviceTimeZone);
        }
        return DateTime.UtcNow;
    }

    private static string? GetString(JsonElement obj, params string[] keys)
    {
        foreach (var k in keys)
            if (obj.TryGetProperty(k, out var v))
                return v.ValueKind switch
                {
                    JsonValueKind.String => v.GetString(),
                    JsonValueKind.Number => v.ToString(),
                    _ => null,
                };
        return null;
    }

    private static int? GetInt(JsonElement obj, params string[] keys)
    {
        foreach (var k in keys)
            if (obj.TryGetProperty(k, out var v))
            {
                if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) return n;
                if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var s)) return s;
            }
        return null;
    }

    /// <summary>Resposta que o firmware espera: success=0 significa OK.</summary>
    private sealed record ProtocolOk
    {
        public int success { get; init; } = 0;
        public string msg { get; init; } = "OK";
    }
}
