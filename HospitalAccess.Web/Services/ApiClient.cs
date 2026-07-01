using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace HospitalAccess.Web.Services;

/// <summary>
/// Cliente tipado para a HospitalAccess.Api (REST). Único ponto de acoplamento com o backend.
///
/// O Bearer token é aplicado aqui, não via AddHttpMessageHandler: DelegatingHandlers
/// registrados nesse pipeline são resolvidos/cacheados pelo IHttpClientFactory de forma
/// independente do escopo por circuito, então um handler que captura AuthState (scoped)
/// acaba preso à instância de AuthState da primeira resolução — todos os circuitos
/// seguintes usam um token errado/nulo e recebem 401 silenciosamente. HttpClient em si,
/// injetado via AddHttpClient&lt;ApiClient&gt;(), é seguro de mutar por instância porque é
/// resolvido por escopo (só o handler interno é compartilhado).
///
/// Nunca usa GetFromJsonAsync puro: ele lança em qualquer status não-2xx (ex.: 401 quando a
/// sessão expira), o que derrubaria a renderização da página com uma exceção não tratada em
/// vez de deixar a página tratar "não autenticado" normalmente.
/// </summary>
public sealed class ApiClient
{
    private readonly HttpClient _http;
    private readonly AuthState _authState;

    public ApiClient(HttpClient http, AuthState authState)
    {
        _http = http;
        _authState = authState;
    }

    private void ApplyAuthHeader() =>
        _http.DefaultRequestHeaders.Authorization =
            _authState.Token is { } token ? new AuthenticationHeaderValue("Bearer", token) : null;

    public async Task<LoginResponse?> LoginAsync(string username, string password)
    {
        var response = await _http.PostAsJsonAsync("api/auth/login", new { username, password });
        return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<LoginResponse>() : null;
    }

    public Task<List<ControllerDto>> GetControllersAsync() => GetJsonOrDefaultAsync("api/controllers", new List<ControllerDto>());

    public async Task<HttpResponseMessage> CreateControllerAsync(CreateControllerRequest request)
    {
        ApplyAuthHeader();
        return await _http.PostAsJsonAsync("api/controllers", request);
    }

    public async Task<(bool ok, string message)> TestConnectionAsync(Guid controllerId)
    {
        ApplyAuthHeader();
        var response = await _http.PostAsync($"api/controllers/{controllerId}/test-connection", null);
        var body = await response.Content.ReadAsStringAsync();
        return (response.IsSuccessStatusCode, body);
    }

    public Task<List<SyncStatusDto>> GetSyncStatusAsync(Guid controllerId) =>
        GetJsonOrDefaultAsync($"api/controllers/{controllerId}/sync-status", new List<SyncStatusDto>());

    public Task<List<DoorDto>> GetDoorsAsync() => GetJsonOrDefaultAsync("api/doors", new List<DoorDto>());

    public async Task<HttpResponseMessage> CreateDoorAsync(CreateDoorRequest request)
    {
        ApplyAuthHeader();
        return await _http.PostAsJsonAsync("api/doors", request);
    }

    public async Task<HttpResponseMessage> OpenDoorAsync(Guid doorId)
    {
        ApplyAuthHeader();
        return await _http.PostAsync($"api/doors/{doorId}/open", null);
    }

    public async Task<HttpResponseMessage> CreateUserAsync(MultipartFormDataContent content)
    {
        ApplyAuthHeader();
        return await _http.PostAsync("api/users", content);
    }

    public async Task<HttpResponseMessage> RevokeUserAsync(Guid userId)
    {
        ApplyAuthHeader();
        return await _http.DeleteAsync($"api/users/{userId}");
    }

    public async Task<HttpResponseMessage> CreateVisitorAsync(CreateVisitorRequest request)
    {
        ApplyAuthHeader();
        return await _http.PostAsJsonAsync("api/visitors", request);
    }

    public async Task<byte[]?> GenerateVisitorQrAsync(Guid visitorId)
    {
        ApplyAuthHeader();
        var response = await _http.PostAsync($"api/visitors/{visitorId}/qrcode", null);
        return response.IsSuccessStatusCode ? await response.Content.ReadAsByteArrayAsync() : null;
    }

    public Task<AccessLogPage?> QueryAccessLogAsync(
        DateTime? from, DateTime? to, uint? userCode, Guid? controllerId, int page, int pageSize)
    {
        var query = new List<string> { $"page={page}", $"pageSize={pageSize}" };
        if (from is not null) query.Add($"from={from:O}");
        if (to is not null) query.Add($"to={to:O}");
        if (userCode is not null) query.Add($"userCode={userCode}");
        if (controllerId is not null) query.Add($"controllerId={controllerId}");

        return GetJsonOrDefaultAsync<AccessLogPage?>($"api/accesslog?{string.Join('&', query)}", null);
    }

    public async Task<byte[]> ExportAccessLogCsvAsync(DateTime? from, DateTime? to)
    {
        ApplyAuthHeader();
        var query = new List<string> { "format=csv" };
        if (from is not null) query.Add($"from={from:O}");
        if (to is not null) query.Add($"to={to:O}");

        var response = await _http.GetAsync($"api/accesslog/export?{string.Join('&', query)}");
        return response.IsSuccessStatusCode ? await response.Content.ReadAsByteArrayAsync() : [];
    }

    /// <summary>GET + desserializa em sucesso; em falha (ex.: 401 por sessão expirada) devolve o valor padrão em vez de lançar.</summary>
    private async Task<T> GetJsonOrDefaultAsync<T>(string url, T fallback)
    {
        ApplyAuthHeader();
        var response = await _http.GetAsync(url);
        if (!response.IsSuccessStatusCode) return fallback;
        return await response.Content.ReadFromJsonAsync<T>() ?? fallback;
    }
}
