using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using HospitalAccess.Api.Options;
using Microsoft.Extensions.Options;

namespace HospitalAccess.Api.Services;

/// <summary>Monta os payloads das chamadas ao HA (puro — testável).</summary>
public static class HomeAssistantPayload
{
    /// <summary>Boas-vindas: o script do HA recebe o quarto, o nome e a URL do JPG pronto.</summary>
    public static object Welcome(string room, string patientName, string welcomeImageUrl) =>
        new { room, patient_name = patientName, welcome_image_url = welcomeImageUrl };

    /// <summary>Limpeza da TV do quarto (alta/transferência).</summary>
    public static object Clear(string room) => new { room };

    /// <summary>"script.boas_vindas_leito" → ("script", "boas_vindas_leito"). Null se malformado.</summary>
    public static (string Domain, string Service)? ParseService(string service)
    {
        if (string.IsNullOrWhiteSpace(service)) return null;
        var dot = service.IndexOf('.');
        if (dot <= 0 || dot == service.Length - 1) return null;
        return (service[..dot], service[(dot + 1)..]);
    }
}

/// <summary>
/// Cliente da REST API do Home Assistant (mesma rede): POST {BaseUrl}/api/services/{domínio}/{serviço}
/// com Bearer token. BEST-EFFORT por contrato: qualquer falha loga Warning e retorna false —
/// a automação da TV nunca pode travar uma internação/alta. Visível no Logs (Dev).
/// </summary>
public sealed class HomeAssistantClient : IDisposable
{
    private readonly HomeAssistantOptions _options;
    private readonly HttpClient _http;
    private readonly ILogger<HomeAssistantClient> _logger;

    public HomeAssistantClient(IOptions<HomeAssistantOptions> options, ILogger<HomeAssistantClient> logger)
    {
        _options = options.Value;
        _logger = logger;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(Math.Max(1, _options.TimeoutSeconds)) };
        if (!string.IsNullOrWhiteSpace(_options.Token))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _options.Token);
    }

    public bool Enabled => _options.Enabled
                           && !string.IsNullOrWhiteSpace(_options.BaseUrl)
                           && !string.IsNullOrWhiteSpace(_options.Token);

    /// <summary>Chama um serviço "domínio.serviço" com o payload dado. Best-effort: false = não chamou/falhou.</summary>
    public async Task<bool> CallServiceAsync(string service, object payload, CancellationToken ct = default)
    {
        if (!Enabled) return false;
        if (HomeAssistantPayload.ParseService(service) is not { } parsed)
        {
            _logger.LogWarning("Home Assistant: serviço '{Service}' malformado (esperado 'dominio.servico').", service);
            return false;
        }

        var url = $"{_options.BaseUrl.TrimEnd('/')}/api/services/{parsed.Domain}/{parsed.Service}";
        try
        {
            using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync(url, content, ct);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("Home Assistant: {Service} devolveu {Status}: {Body}",
                    service, (int)response.StatusCode, body.Length > 300 ? body[..300] : body);
                return false;
            }
            _logger.LogInformation("Home Assistant: {Service} chamado com sucesso.", service);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Home Assistant: falha ao chamar {Service} em {Url}.", service, url);
            return false;
        }
    }

    public void Dispose() => _http.Dispose();
}
