using System.Text;
using System.Text.Json;
using HospitalAccess.Domain.Entities;
using HospitalAccess.Infrastructure.Devices;
using HospitalAccess.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HospitalAccess.Api.Services;

/// <summary>Resultado da obtenção do QR real (o texto que o aparelho valida) de um controlador.</summary>
public sealed record DeviceQrResult(string QrBase64, string Payload, Guid ControllerId, string ControllerName, bool Provisioned);

/// <summary>
/// Obtém o QRCode de acesso LENDO-O do controlador (via API HTTP), porque o firmware valida o QR
/// contra um texto que ele mesmo gera (com um timestamp em microssegundos irreproduzível). Se a
/// pessoa ainda não tem QRCode no aparelho, provisiona via <c>/api/People/New</c> (que faz o firmware
/// cunhar o QRCode) e relê. Ver DeviceHttpClient e o relatório de engenharia reversa.
/// </summary>
public sealed class DeviceQrService
{
    // Faixa aceita pelo firmware para ExpirationDate (unix seconds); fora disso o People/New rejeita
    // com errCode=11 ("ExpirationDate value error"). 4102415999 (~2100) = "nunca expira".
    private const long ExpMin = 946659600;
    private const long ExpMax = 4102415999;

    private readonly AccessDbContext _db;
    private readonly DeviceHttpClientFactory _factory;
    private readonly ILogger<DeviceQrService> _logger;

    public DeviceQrService(AccessDbContext db, DeviceHttpClientFactory factory, ILogger<DeviceQrService> logger)
    {
        _db = db;
        _factory = factory;
        _logger = logger;
    }

    /// <summary>
    /// Retorna o QR real do usuário. Tenta cada controlador (com API HTTP) onde o usuário tem
    /// permissão: lê o cadastro; se faltar QRCode, provisiona e relê. Devolve null se nenhum
    /// controlador HTTP produziu um QR (o chamador cai no fluxo manual de colar).
    /// </summary>
    public async Task<DeviceQrResult?> GetForUserAsync(User user, CancellationToken ct = default)
    {
        var controllerIds = user.Permissions.Select(p => p.ControllerId).Distinct().ToList();
        if (controllerIds.Count == 0) return null;

        var controllers = await _db.Controllers
            .Where(c => controllerIds.Contains(c.Id))
            .ToListAsync(ct);

        var httpControllers = controllers.Where(_factory.CanUseHttp).ToList();
        if (httpControllers.Count == 0) return null;

        DeviceHttpException? lastError = null;
        foreach (var controller in httpControllers)
        {
            try
            {
                var result = await ReadOrProvisionAsync(controller, user, ct);
                if (result != null) return result;
            }
            catch (DeviceHttpException ex)
            {
                lastError = ex;
                _logger.LogWarning("QR: falha no controlador {Controller} para {UserCode}: {Msg}", controller.Name, user.UserCode, ex.Message);
            }
        }

        if (lastError != null) throw lastError;
        return null;
    }

    /// <summary>
    /// Remove a pessoa (e, com ela, o QRCode guardado) dos controladores informados via HTTP
    /// <c>/api/People/Delete</c>. Usado ao trocar o visitante de quarto: invalida o QR antigo.
    /// Best-effort — falhas são logadas, não propagadas.
    /// </summary>
    public async Task RemoveFromControllersAsync(uint userCode, IEnumerable<Guid> controllerIds, CancellationToken ct = default)
    {
        var ids = controllerIds.Distinct().ToList();
        if (ids.Count == 0) return;

        var controllers = await _db.Controllers.Where(c => ids.Contains(c.Id)).ToListAsync(ct);
        foreach (var controller in controllers.Where(_factory.CanUseHttp))
        {
            try
            {
                await using var client = _factory.Create(controller);
                await client.EnsureAuthenticatedAsync(ct);
                await client.DeleteUsersAsync(new[] { userCode.ToString() }, ct: ct);
                await PersistTokenIfRefreshedAsync(controller, client, ct);
                _logger.LogInformation("QR: removido {UserCode} do controlador {Controller} via HTTP.", userCode, controller.Name);
            }
            catch (DeviceHttpException ex)
            {
                _logger.LogWarning("Falha ao remover {UserCode} do controlador {Controller} via HTTP: {Msg}", userCode, controller.Name, ex.Message);
            }
        }
    }

    private async Task<DeviceQrResult?> ReadOrProvisionAsync(Controller controller, User user, CancellationToken ct)
    {
        var userId = user.UserCode.ToString();
        await using var client = _factory.Create(controller);
        await client.EnsureAuthenticatedAsync(ct);

        var provisioned = false;
        var detail = await client.GetUserDetailAsync(userId, ct);
        var qr = detail is { } d ? DeviceHttpClient.ExtractQrCode(d) : null;

        // Sem cadastro (ou sem QRCode): provisiona via People/New (o firmware cunha o QRCode) e relê.
        if (string.IsNullOrEmpty(qr))
        {
            var peopleJson = BuildVisitorPeopleJson(user);
            await client.AddOrUpdateUserAsync(peopleJson, photoBytes: null, ct: ct);
            provisioned = true;
            detail = await client.GetUserDetailAsync(userId, ct);
            qr = detail is { } d2 ? DeviceHttpClient.ExtractQrCode(d2) : null;
        }

        await PersistTokenIfRefreshedAsync(controller, client, ct);

        if (string.IsNullOrEmpty(qr)) return null;

        var payload = TryDecodeBase64(qr);
        _logger.LogInformation("QR: obtido do controlador {Controller} para {UserCode} (provisionado={Prov}): {Payload}",
            controller.Name, user.UserCode, provisioned, payload);
        return new DeviceQrResult(qr, payload, controller.Id, controller.Name, provisioned);
    }

    /// <summary>Monta o PeopleJson de um visitante (sem foto/face). ExpirationDate em unix seconds (UTC absoluto), clampado.</summary>
    private static Dictionary<string, object> BuildVisitorPeopleJson(User user)
    {
        var expUnix = ClampExpiration(user.ValidUntil);
        var code = user.UserCode.ToString();
        return new Dictionary<string, object>
        {
            ["UserID"] = code,
            ["Code"] = code,
            ["Name"] = user.Name ?? "",
            ["Department"] = "",
            ["Job"] = "",
            ["IdentityCard"] = "",
            ["CardNum"] = user.CardNumber?.ToString() ?? "0",
            ["Password"] = "",
            ["Timegroup"] = user.TimeGroup < 1 ? 1 : user.TimeGroup,
            ["ExpirationDate"] = expUnix,
            ["AccessType"] = 0,
            ["OpenTimes"] = 65535,
            ["Attachment"] = "",
            ["QRCode"] = "",
            ["KeepOpen"] = 0,
            ["PhotoMD5"] = "",
            ["PhotoLen"] = 0,
            ["Photo"] = "",
            ["Elevators"] = "",
            ["Holidays"] = "",
            ["Fingerprints"] = Array.Empty<object>(),
            ["Palmveins"] = Array.Empty<object>(),
            ["FaceFeature"] = "",
        };
    }

    private static long ClampExpiration(DateTime? validUntil)
    {
        if (validUntil is null) return ExpMax;
        var ts = ((DateTimeOffset)DateTime.SpecifyKind(validUntil.Value, DateTimeKind.Utc)).ToUnixTimeSeconds();
        return Math.Clamp(ts, ExpMin, ExpMax);
    }

    private async Task PersistTokenIfRefreshedAsync(Controller controller, DeviceHttpClient client, CancellationToken ct)
    {
        if (!client.TokenRefreshed || string.IsNullOrEmpty(client.Token)) return;
        controller.ApiToken = client.Token;
        controller.ApiTokenUpdatedAtUtc = DateTime.UtcNow;
        try { await _db.SaveChangesAsync(ct); }
        catch (Exception ex) { _logger.LogWarning(ex, "Falha ao persistir token HTTP do controlador {Controller}.", controller.Name); }
    }

    private static string TryDecodeBase64(string b64)
    {
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(b64)); }
        catch { return "<não-Base64>"; }
    }
}
