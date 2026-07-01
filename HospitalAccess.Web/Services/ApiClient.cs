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

    // ---- Controladores (= portas) ----

    public Task<List<ControllerDto>> GetControllersAsync() => GetJsonOrDefaultAsync("api/controllers", new List<ControllerDto>());

    public Task<ControllerDetailDto?> GetControllerAsync(Guid id) =>
        GetJsonOrDefaultAsync<ControllerDetailDto?>($"api/controllers/{id}", null);

    public async Task<HttpResponseMessage> CreateControllerAsync(CreateControllerRequest request)
    {
        ApplyAuthHeader();
        return await _http.PostAsJsonAsync("api/controllers", request);
    }

    public async Task<HttpResponseMessage> UpdateControllerAsync(Guid id, UpdateControllerRequest request)
    {
        ApplyAuthHeader();
        return await _http.PutAsJsonAsync($"api/controllers/{id}", request);
    }

    public async Task<HttpResponseMessage> DeleteControllerAsync(Guid id)
    {
        ApplyAuthHeader();
        return await _http.DeleteAsync($"api/controllers/{id}");
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

    public Task<(bool ok, string message)> OpenDoorAsync(Guid controllerId) => RunDoorCommand(controllerId, "open");
    public Task<(bool ok, string message)> CloseDoorAsync(Guid controllerId) => RunDoorCommand(controllerId, "close");
    public Task<(bool ok, string message)> HoldDoorOpenAsync(Guid controllerId) => RunDoorCommand(controllerId, "hold-open");
    public Task<(bool ok, string message)> LockDoorAsync(Guid controllerId) => RunDoorCommand(controllerId, "lock");
    public Task<(bool ok, string message)> UnlockDoorAsync(Guid controllerId) => RunDoorCommand(controllerId, "unlock");

    private async Task<(bool ok, string message)> RunDoorCommand(Guid controllerId, string action)
    {
        ApplyAuthHeader();
        var response = await _http.PostAsync($"api/controllers/{controllerId}/{action}", null);
        var body = response.IsSuccessStatusCode ? "" : await response.Content.ReadAsStringAsync();
        return (response.IsSuccessStatusCode, body);
    }

    // ---- Rede ----

    public Task<ControllerNetworkInfo?> GetNetworkAsync(Guid controllerId) =>
        GetJsonOrDefaultAsync<ControllerNetworkInfo?>($"api/controllers/{controllerId}/network", null);

    public async Task<HttpResponseMessage> UpdateNetworkAsync(Guid controllerId, ControllerNetworkInfo info)
    {
        ApplyAuthHeader();
        return await _http.PutAsJsonAsync($"api/controllers/{controllerId}/network", info);
    }

    public async Task<List<DiscoveredController>> DiscoverControllersAsync(int udpPort, int scanSeconds)
    {
        ApplyAuthHeader();
        var response = await _http.PostAsync($"api/controllers/discover?udpPort={udpPort}&scanSeconds={scanSeconds}", null);
        if (!response.IsSuccessStatusCode) return [];
        return await response.Content.ReadFromJsonAsync<List<DiscoveredController>>() ?? [];
    }

    // ---- Relógio ----

    public Task<DateTime?> GetClockAsync(Guid controllerId) => GetJsonOrDefaultAsync<DateTime?>($"api/controllers/{controllerId}/clock", null);

    public async Task<(bool ok, string message)> SyncClockAsync(Guid controllerId)
    {
        ApplyAuthHeader();
        var response = await _http.PostAsync($"api/controllers/{controllerId}/clock/sync", null);
        var body = response.IsSuccessStatusCode ? "" : await response.Content.ReadAsStringAsync();
        return (response.IsSuccessStatusCode, body);
    }

    // ---- Alarmes (configuração por controlador) ----

    public Task<AlarmSettings?> GetAlarmSettingsAsync(Guid controllerId) =>
        GetJsonOrDefaultAsync<AlarmSettings?>($"api/controllers/{controllerId}/alarm-settings", null);

    public async Task<HttpResponseMessage> UpdateAlarmSettingsAsync(Guid controllerId, AlarmSettings settings)
    {
        ApplyAuthHeader();
        return await _http.PutAsJsonAsync($"api/controllers/{controllerId}/alarm-settings", settings);
    }

    public async Task<(bool ok, string message)> ClearAlarmAsync(Guid controllerId)
    {
        ApplyAuthHeader();
        var response = await _http.PostAsync($"api/controllers/{controllerId}/alarm-clear", null);
        var body = response.IsSuccessStatusCode ? "" : await response.Content.ReadAsStringAsync();
        return (response.IsSuccessStatusCode, body);
    }

    // ---- Ajustes locais (quiosque) ----

    public Task<KioskSettings?> GetKioskSettingsAsync(Guid controllerId) =>
        GetJsonOrDefaultAsync<KioskSettings?>($"api/controllers/{controllerId}/kiosk-settings", null);

    public async Task<HttpResponseMessage> UpdateKioskSettingsAsync(Guid controllerId, KioskSettings settings)
    {
        ApplyAuthHeader();
        return await _http.PutAsJsonAsync($"api/controllers/{controllerId}/kiosk-settings", settings);
    }

    // ---- Leitura reversa / auditoria ----

    public Task<PersonnelAudit?> GetPersonnelAuditAsync(Guid controllerId) =>
        GetJsonOrDefaultAsync<PersonnelAudit?>($"api/controllers/{controllerId}/personnel-audit", null);

    // ---- Foto do evento ----

    public async Task<(bool ok, string message)> DownloadEventPhotosAsync(Guid controllerId, int quantity)
    {
        ApplyAuthHeader();
        var response = await _http.PostAsync($"api/controllers/{controllerId}/event-photos/download?quantity={quantity}", null);
        var body = await response.Content.ReadAsStringAsync();
        return (response.IsSuccessStatusCode, body);
    }

    public Task<List<EventPhotoListItem>> GetEventPhotosAsync(Guid controllerId) =>
        GetJsonOrDefaultAsync($"api/controllers/{controllerId}/event-photos", new List<EventPhotoListItem>());

    public async Task<byte[]?> GetEventPhotoImageAsync(Guid photoId)
    {
        ApplyAuthHeader();
        var response = await _http.GetAsync($"api/controllers/event-photos/{photoId}/image");
        return response.IsSuccessStatusCode ? await response.Content.ReadAsByteArrayAsync() : null;
    }

    // ---- Feriados ----

    public Task<List<HolidayDto>> GetHolidaysAsync() => GetJsonOrDefaultAsync("api/holidays", new List<HolidayDto>());

    public async Task<HttpResponseMessage> CreateHolidayAsync(HolidayRequest request)
    {
        ApplyAuthHeader();
        return await _http.PostAsJsonAsync("api/holidays", request);
    }

    public async Task<HttpResponseMessage> UpdateHolidayAsync(Guid id, HolidayRequest request)
    {
        ApplyAuthHeader();
        return await _http.PutAsJsonAsync($"api/holidays/{id}", request);
    }

    public async Task<HttpResponseMessage> DeleteHolidayAsync(Guid id)
    {
        ApplyAuthHeader();
        return await _http.DeleteAsync($"api/holidays/{id}");
    }

    public async Task<(bool ok, string message)> SyncAllHolidaysAsync()
    {
        ApplyAuthHeader();
        var response = await _http.PostAsync("api/holidays/sync-all", null);
        var body = await response.Content.ReadAsStringAsync();
        return (response.IsSuccessStatusCode, body);
    }

    // ---- Grade horária ----

    public Task<List<TimeGroupScheduleDto>> GetTimeGroupsAsync() => GetJsonOrDefaultAsync("api/timegroups", new List<TimeGroupScheduleDto>());

    public async Task<HttpResponseMessage> CreateTimeGroupAsync(TimeGroupScheduleRequest request)
    {
        ApplyAuthHeader();
        return await _http.PostAsJsonAsync("api/timegroups", request);
    }

    public async Task<HttpResponseMessage> UpdateTimeGroupAsync(Guid id, TimeGroupScheduleRequest request)
    {
        ApplyAuthHeader();
        return await _http.PutAsJsonAsync($"api/timegroups/{id}", request);
    }

    public async Task<HttpResponseMessage> DeleteTimeGroupAsync(Guid id)
    {
        ApplyAuthHeader();
        return await _http.DeleteAsync($"api/timegroups/{id}");
    }

    public async Task<(bool ok, string message)> SyncAllTimeGroupsAsync()
    {
        ApplyAuthHeader();
        var response = await _http.PostAsync("api/timegroups/sync-all", null);
        var body = await response.Content.ReadAsStringAsync();
        return (response.IsSuccessStatusCode, body);
    }

    // ---- Log de alarmes ----

    public Task<AlarmEventPage?> QueryAlarmEventsAsync(
        DateTime? from, DateTime? to, Guid? controllerId, string? kind, int page, int pageSize)
    {
        var query = new List<string> { $"page={page}", $"pageSize={pageSize}" };
        if (from is not null) query.Add($"from={from:O}");
        if (to is not null) query.Add($"to={to:O}");
        if (controllerId is not null) query.Add($"controllerId={controllerId}");
        if (!string.IsNullOrEmpty(kind)) query.Add($"kind={kind}");

        return GetJsonOrDefaultAsync<AlarmEventPage?>($"api/alarmevents?{string.Join('&', query)}", null);
    }

    public async Task<byte[]> ExportAlarmEventsCsvAsync(DateTime? from, DateTime? to, Guid? controllerId, string? kind)
    {
        ApplyAuthHeader();
        var query = new List<string>();
        if (from is not null) query.Add($"from={from:O}");
        if (to is not null) query.Add($"to={to:O}");
        if (controllerId is not null) query.Add($"controllerId={controllerId}");
        if (!string.IsNullOrEmpty(kind)) query.Add($"kind={kind}");

        var response = await _http.GetAsync($"api/alarmevents/export?{string.Join('&', query)}");
        return response.IsSuccessStatusCode ? await response.Content.ReadAsByteArrayAsync() : [];
    }

    // ---- Grupos de usuários ----

    public Task<List<UserGroupDto>> GetUserGroupsAsync() => GetJsonOrDefaultAsync("api/usergroups", new List<UserGroupDto>());

    public async Task<HttpResponseMessage> CreateUserGroupAsync(UserGroupRequest request)
    {
        ApplyAuthHeader();
        return await _http.PostAsJsonAsync("api/usergroups", request);
    }

    public async Task<HttpResponseMessage> UpdateUserGroupAsync(Guid id, UserGroupRequest request)
    {
        ApplyAuthHeader();
        return await _http.PutAsJsonAsync($"api/usergroups/{id}", request);
    }

    public async Task<HttpResponseMessage> DeleteUserGroupAsync(Guid id)
    {
        ApplyAuthHeader();
        return await _http.DeleteAsync($"api/usergroups/{id}");
    }

    // ---- Usuários permanentes ----

    public Task<List<UserListItemDto>> GetUsersAsync() => GetJsonOrDefaultAsync("api/users", new List<UserListItemDto>());

    public Task<UserDetailDto?> GetUserAsync(Guid id) => GetJsonOrDefaultAsync<UserDetailDto?>($"api/users/{id}", null);

    public async Task<HttpResponseMessage> CreateUserAsync(MultipartFormDataContent content)
    {
        ApplyAuthHeader();
        return await _http.PostAsync("api/users", content);
    }

    public async Task<HttpResponseMessage> UpdateUserAsync(Guid id, MultipartFormDataContent content)
    {
        ApplyAuthHeader();
        var request = new HttpRequestMessage(HttpMethod.Put, $"api/users/{id}") { Content = content };
        return await _http.SendAsync(request);
    }

    public async Task<HttpResponseMessage> DeleteUserAsync(Guid id)
    {
        ApplyAuthHeader();
        return await _http.DeleteAsync($"api/users/{id}");
    }

    // ---- Visitantes / temporários ----

    public Task<List<VisitorListItemDto>> GetVisitorsAsync() => GetJsonOrDefaultAsync("api/visitors", new List<VisitorListItemDto>());

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

    public async Task<HttpResponseMessage> RevokeVisitorAsync(Guid id)
    {
        ApplyAuthHeader();
        return await _http.DeleteAsync($"api/visitors/{id}");
    }

    // ---- Log de acessos ----

    public Task<AccessLogPage?> QueryAccessLogAsync(
        DateTime? from, DateTime? to, uint? userCode, Guid? controllerId, string? method, int page, int pageSize)
    {
        var query = new List<string> { $"page={page}", $"pageSize={pageSize}" };
        if (from is not null) query.Add($"from={from:O}");
        if (to is not null) query.Add($"to={to:O}");
        if (userCode is not null) query.Add($"userCode={userCode}");
        if (controllerId is not null) query.Add($"controllerId={controllerId}");
        if (!string.IsNullOrEmpty(method)) query.Add($"method={method}");

        return GetJsonOrDefaultAsync<AccessLogPage?>($"api/accesslog?{string.Join('&', query)}", null);
    }

    public async Task<byte[]> ExportAccessLogCsvAsync(DateTime? from, DateTime? to, uint? userCode, Guid? controllerId, string? method)
    {
        ApplyAuthHeader();
        var query = new List<string> { "format=csv" };
        if (from is not null) query.Add($"from={from:O}");
        if (to is not null) query.Add($"to={to:O}");
        if (userCode is not null) query.Add($"userCode={userCode}");
        if (controllerId is not null) query.Add($"controllerId={controllerId}");
        if (!string.IsNullOrEmpty(method)) query.Add($"method={method}");

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
