using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

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
/// A configuração (enabled/URL/token/serviços) é lida POR CHAMADA do RuntimeSettingsProvider:
/// salvar na tela de Configurações vale imediatamente, sem restart.
/// </summary>
public sealed class HomeAssistantClient : IDisposable
{
    private readonly RuntimeSettingsProvider _settings;
    private readonly HttpClient _http;
    private readonly ILogger<HomeAssistantClient> _logger;

    public HomeAssistantClient(RuntimeSettingsProvider settings, ILogger<HomeAssistantClient> logger)
    {
        _settings = settings;
        _logger = logger;
        // Timeout por requisição via CTS (o TimeoutSeconds pode variar); o do HttpClient fica alto.
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
    }

    public bool Enabled => _settings.HomeAssistant.Ready;

    /// <summary>Chama um serviço "domínio.serviço" com o payload dado. Best-effort: false = não chamou/falhou.</summary>
    public async Task<bool> CallServiceAsync(string service, object payload, CancellationToken ct = default)
    {
        var cfg = _settings.HomeAssistant;
        if (!cfg.Ready) return false;
        if (HomeAssistantPayload.ParseService(service) is not { } parsed)
        {
            _logger.LogWarning("Home Assistant: serviço '{Service}' malformado (esperado 'dominio.servico').", service);
            return false;
        }

        var url = $"{cfg.BaseUrl.TrimEnd('/')}/api/services/{parsed.Domain}/{parsed.Service}";
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, cfg.TimeoutSeconds)));
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.Token);
            using var response = await _http.SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(timeout.Token);
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

    /// <summary>
    /// Sonda a REST API do HA (GET /api/ com o token) — botão "Testar conexão" das Configurações.
    /// Devolve (ok, mensagem legível).
    /// </summary>
    public async Task<(bool Ok, string Message)> TestAsync(CancellationToken ct = default)
    {
        var cfg = _settings.HomeAssistant;
        if (string.IsNullOrWhiteSpace(cfg.BaseUrl)) return (false, "URL do Home Assistant não configurada.");
        if (string.IsNullOrWhiteSpace(cfg.Token)) return (false, "Token do Home Assistant não configurado.");

        var url = $"{cfg.BaseUrl.TrimEnd('/')}/api/";
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, cfg.TimeoutSeconds)));
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.Token);
            using var response = await _http.SendAsync(request, timeout.Token);
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                return (false, "O Home Assistant respondeu 401 — token inválido ou expirado.");
            if (!response.IsSuccessStatusCode)
                return (false, $"O Home Assistant respondeu {(int)response.StatusCode}.");
            return (true, cfg.Enabled
                ? "Conexão OK — API do Home Assistant respondendo."
                : "Conexão OK — mas a integração está DESLIGADA (ative para as boas-vindas dispararem).");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (false, $"Sem resposta em {cfg.TimeoutSeconds}s — verifique a URL e a rede.");
        }
        catch (Exception ex)
        {
            return (false, $"Falha ao conectar: {ex.Message}");
        }
    }

    public void Dispose() => _http.Dispose();
}
