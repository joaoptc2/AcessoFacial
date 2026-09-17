using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HospitalAccess.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace HospitalAccess.Infrastructure.Devices;

/// <summary>
/// Cliente da API REST LOCAL do painel web de um controlador FC-8190H (a mesma API que o próprio
/// painel usa). Existe porque o SDK binário (porta 8000) NÃO expõe o campo <c>QRCode</c> por pessoa —
/// e é justamente esse texto, cunhado pelo aparelho com um timestamp em microssegundos, que o leitor
/// valida. A única forma de obter um QR que abre é LER via <c>/api/People/GetDetail</c>.
///
/// Todo o comportamento foi portado de um sistema de referência que fez a engenharia reversa do
/// painel. Pontos críticos (ver comentários por método):
/// <list type="bullet">
///   <item>Login: senha = <c>MD5(Hash + senha + Hash)</c> em HEX MAIÚSCULO, Hash = GUID reenviado no corpo. Sem campo de usuário.</item>
///   <item>O firmware responde HTTP 200 mesmo em erro — a falha vem no corpo (<c>result:false</c> + <c>errCode</c>).</item>
///   <item>O token fica em <c>content.token</c> (aninhado). Cada login invalida o anterior; re-logamos em 401.</item>
///   <item><c>/api/People/New</c> é multipart com pegadinhas byte-a-byte (ver <see cref="BuildMultipartBody"/>).</item>
/// </list>
///
/// Use como <c>await using</c>. Não é thread-safe — um por operação.
/// </summary>
public sealed class DeviceHttpClient : IAsyncDisposable
{
    private readonly HttpClient _http;
    private readonly string _loginPath;
    private readonly string _password;
    private readonly ILogger _logger;
    private readonly string _controllerLabel;

    private string? _token;

    /// <summary>Token atual (pode ter sido renovado durante a operação — ver <see cref="TokenRefreshed"/>).</summary>
    public string? Token => _token;

    /// <summary>True se um novo login ocorreu nesta sessão — o chamador deve persistir <see cref="Token"/> no controlador.</summary>
    public bool TokenRefreshed { get; private set; }

    internal DeviceHttpClient(HttpClient http, string loginPath, string password, string? cachedToken, string controllerLabel, ILogger logger)
    {
        _http = http;
        _loginPath = loginPath;
        _password = password;
        _token = string.IsNullOrEmpty(cachedToken) ? null : cachedToken;
        _controllerLabel = controllerLabel;
        _logger = logger;
    }

    public ValueTask DisposeAsync()
    {
        _http.Dispose();
        return ValueTask.CompletedTask;
    }

    // ------------------------------------------------------------------ //
    // Autenticação
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Login. Algoritmo extraído do JS do painel (/page/login.html):
    /// <c>MD5(Hash + senha + Hash)</c> em hex MAIÚSCULO, Hash = GUID v4 reenviado no corpo; sem usuário.
    /// Corpo: <c>{"password", "rememberMe":"true", "Hash"}</c>. Token em <c>content.token</c>.
    /// O firmware devolve 200 mesmo em erro → conferir <c>result:false</c>.
    /// </summary>
    public async Task LoginAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_password))
            throw new DeviceHttpException($"[{_controllerLabel}] sem senha do painel web (ApiPassword nem padrão global configurados).");

        var clientHash = Guid.NewGuid().ToString();
        var passwordHash = ComputeLoginPasswordHash(clientHash, _password);

        var bodyJson = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["password"] = passwordHash,
            ["rememberMe"] = "true",
            ["Hash"] = clientHash,
        });

        using var req = new HttpRequestMessage(HttpMethod.Post, _loginPath)
        {
            Content = new StringContent(bodyJson, Encoding.UTF8, "application/json"),
        };
        HttpResponseMessage resp;
        try { resp = await _http.SendAsync(req, ct); }
        catch (HttpRequestException ex) { throw new DeviceHttpException($"[{_controllerLabel}] erro de rede no login: {ex.Message}", inner: ex); }

        if (resp.StatusCode != HttpStatusCode.OK)
            throw new DeviceHttpException($"[{_controllerLabel}] login status={(int)resp.StatusCode}", status: (int)resp.StatusCode);

        using var doc = await ReadJsonAsync(resp, ct);
        var root = doc.RootElement;
        if (root.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.False)
        {
            var (code, msg) = ExtractError(root);
            throw new DeviceHttpException($"[{_controllerLabel}] login rejeitado: errCode={code} error={msg}", status: 200, errCode: code);
        }

        var token = ExtractToken(root);
        if (string.IsNullOrEmpty(token))
            throw new DeviceHttpException($"[{_controllerLabel}] login sem token em content.token", status: 200);

        _token = token;
        TokenRefreshed = true;
        _logger.LogInformation("[{Controller}] login OK, token cacheado", _controllerLabel);
    }

    /// <summary>
    /// Valida o token atual com <c>/api/User/CheckLoginToken</c>. O firmware devolve HTTP 200
    /// MESMO com token inválido (a rejeição vem no corpo, <c>result:false</c>) — confiar só no
    /// status fazia um token morto (aparelho reiniciado) passar na validação e estourar depois
    /// como errCode=10000 "Token is invalid" no GetDetail.
    /// </summary>
    public async Task<bool> CheckTokenAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_token)) return false;
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/User/CheckLoginToken");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        try
        {
            var resp = await _http.SendAsync(req, ct);
            if (resp.StatusCode != HttpStatusCode.OK) return false;
            using var doc = await ReadJsonAsync(resp, ct);
            return !(doc.RootElement.ValueKind == JsonValueKind.Object
                     && doc.RootElement.TryGetProperty("result", out var r)
                     && r.ValueKind == JsonValueKind.False);
        }
        catch (HttpRequestException) { return false; }
        catch (DeviceHttpException) { return false; } // corpo não-JSON = token não confiável
    }

    /// <summary>Garante um token válido: sem token → login; com token → valida e re-loga se preciso.</summary>
    public async Task EnsureAuthenticatedAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_token)) { await LoginAsync(ct); return; }
        if (await CheckTokenAsync(ct)) return;
        _logger.LogInformation("[{Controller}] token inválido, re-login", _controllerLabel);
        await LoginAsync(ct);
    }

    // ------------------------------------------------------------------ //
    // Wrapper genérico (envelope {result,content,errCode,error})
    // ------------------------------------------------------------------ //

    /// <summary>
    /// POST JSON autenticado. Desembrulha o envelope uniforme do FC-8190H:
    /// <c>result:false</c> → lança com errCode/error (mesmo em HTTP 200); <c>result:true</c> → devolve
    /// o <c>content</c> (ou o payload inteiro). Re-loga uma vez em 401.
    /// </summary>
    private async Task<JsonElement> PostJsonAsync(string path, object? body, CancellationToken ct, bool retryOn401 = true)
    {
        var bodyJson = JsonSerializer.Serialize(body ?? new { });
        using var req = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(bodyJson, Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrEmpty(_token))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);

        HttpResponseMessage resp;
        try { resp = await _http.SendAsync(req, ct); }
        catch (HttpRequestException ex) { throw new DeviceHttpException($"[{_controllerLabel}] POST {path} erro: {ex.Message}", inner: ex); }

        if (resp.StatusCode == HttpStatusCode.Unauthorized && retryOn401)
        {
            _logger.LogInformation("[{Controller}] {Path} -> 401, re-login", _controllerLabel, path);
            await LoginAsync(ct);
            return await PostJsonAsync(path, body, ct, retryOn401: false);
        }
        if (resp.StatusCode != HttpStatusCode.OK)
            throw new DeviceHttpException($"[{_controllerLabel}] POST {path} status={(int)resp.StatusCode}", status: (int)resp.StatusCode);

        var doc = await ReadJsonAsync(resp, ct);
        try
        {
            return Unwrap(doc, path);
        }
        catch (DeviceHttpException ex) when (retryOn401 && IsTokenError(ex))
        {
            // Token inválido vem como HTTP 200 + errCode=10000 no corpo (o firmware NÃO usa 401)
            // — típico após reinício do aparelho matar o token cacheado. Re-loga e repete UMA vez.
            _logger.LogInformation("[{Controller}] {Path} -> token inválido (errCode {Code}), re-login", _controllerLabel, path, ex.ErrCode);
            await LoginAsync(ct);
            return await PostJsonAsync(path, body, ct, retryOn401: false);
        }
    }

    /// <summary>errCode 10000 / "Token is invalid" no envelope = autenticação, não erro do endpoint.</summary>
    internal static bool IsTokenError(DeviceHttpException ex) =>
        ex.ErrCode == 10000 || ex.Message.Contains("Token is invalid", StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ //
    // Endpoints concretos
    // ------------------------------------------------------------------ //

    /// <summary><c>GET /api/GetDeviceSN</c> — sem auth. Formatos variam ({SN}/{sn}/{Data}).</summary>
    public async Task<string?> GetDeviceSnAsync(CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.GetAsync("/api/GetDeviceSN", ct);
            if (resp.StatusCode != HttpStatusCode.OK) return null;
            using var doc = await ReadJsonAsync(resp, ct);
            var root = doc.RootElement;
            foreach (var key in new[] { "SN", "sn" })
                if (root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String) return v.GetString();
            if (root.TryGetProperty("Data", out var d) && d.ValueKind == JsonValueKind.String) return d.GetString();
            return null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// <c>POST /api/People/GetDetail</c> — devolve o cadastro completo da pessoa (inclui <c>QRCode</c>,
    /// <c>FaceFeature</c>, <c>Photo</c> etc.). Devolve <c>null</c> se não existir.
    /// </summary>
    public async Task<JsonElement?> GetUserDetailAsync(string userId, CancellationToken ct = default)
    {
        try
        {
            var content = await PostJsonAsync("/api/People/GetDetail", new Dictionary<string, string> { ["UserID"] = userId }, ct);
            return content.ValueKind == JsonValueKind.Object ? content.Clone() : null;
        }
        catch (DeviceHttpException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    /// <summary><c>POST /api/People/GetNewID</c> — próximo slot livre. Aceita NewUserID/UserID/ID.</summary>
    public async Task<string> AllocateUserIdAsync(CancellationToken ct = default)
    {
        var content = await PostJsonAsync("/api/People/GetNewID", new { }, ct);
        foreach (var key in new[] { "NewUserID", "UserID", "userID", "UserId", "userId", "ID", "id" })
            if (content.TryGetProperty(key, out var v) && v.ValueKind is JsonValueKind.Number or JsonValueKind.String)
                return v.ToString();
        throw new DeviceHttpException($"[{_controllerLabel}] GetNewID sem NewUserID/UserID");
    }

    /// <summary><c>POST /api/People/Delete</c> — apaga por UserID (slot interno).</summary>
    public async Task DeleteUsersAsync(IEnumerable<string> userIds, bool deleteAll = false, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object>
        {
            ["UserIDs"] = userIds.Select(u => u).ToArray(),
            ["DeleteALL"] = deleteAll ? 1 : 0,
        };
        await PostJsonAsync("/api/People/Delete", body, ct);
    }

    /// <summary>
    /// <c>POST /api/People/New</c> (cria E edita — Add=Edit por UserID). SEMPRE multipart, montado
    /// byte-a-byte (ver <see cref="BuildMultipartBody"/>): a parte <c>PeopleJson</c> NÃO pode ter
    /// Content-Type (senão o firmware fecha a conexão sem responder); a foto usa <c>image/jpg</c>.
    /// Passe <paramref name="photoBytes"/> null para edição sem troca de foto.
    /// </summary>
    public async Task<JsonElement> AddOrUpdateUserAsync(object peopleJson, byte[]? photoBytes = null, string photoFilename = "1.jpg", CancellationToken ct = default)
    {
        await EnsureAuthenticatedAsync(ct);
        var peopleJsonStr = JsonSerializer.Serialize(peopleJson, CompactJson);
        var (body, contentType) = BuildMultipartBody(peopleJsonStr, photoBytes, photoFilename);

        async Task<HttpResponseMessage> Send()
        {
            var req = new HttpRequestMessage(HttpMethod.Post, "/api/People/New");
            var httpContent = new ByteArrayContent(body);
            httpContent.Headers.TryAddWithoutValidation("Content-Type", contentType);
            req.Content = httpContent;
            if (!string.IsNullOrEmpty(_token))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            return await _http.SendAsync(req, ct);
        }

        HttpResponseMessage resp;
        try { resp = await Send(); }
        catch (HttpRequestException ex) { throw new DeviceHttpException($"[{_controllerLabel}] POST /api/People/New erro: {ex.Message}", inner: ex); }

        if (resp.StatusCode == HttpStatusCode.Unauthorized)
        {
            await LoginAsync(ct);
            try { resp = await Send(); }
            catch (HttpRequestException ex) { throw new DeviceHttpException($"[{_controllerLabel}] POST /api/People/New erro: {ex.Message}", inner: ex); }
        }
        if (resp.StatusCode != HttpStatusCode.OK)
            throw new DeviceHttpException($"[{_controllerLabel}] POST /api/People/New status={(int)resp.StatusCode}", status: (int)resp.StatusCode);

        var doc = await ReadJsonAsync(resp, ct);
        try
        {
            return Unwrap(doc, "/api/People/New");
        }
        catch (DeviceHttpException ex) when (IsTokenError(ex))
        {
            // Mesmo caso do PostJsonAsync: token morto vem como 200 + errCode=10000, nunca 401.
            _logger.LogInformation("[{Controller}] /api/People/New -> token inválido, re-login", _controllerLabel);
            await LoginAsync(ct);
            try { resp = await Send(); }
            catch (HttpRequestException ex2) { throw new DeviceHttpException($"[{_controllerLabel}] POST /api/People/New erro: {ex2.Message}", inner: ex2); }
            if (resp.StatusCode != HttpStatusCode.OK)
                throw new DeviceHttpException($"[{_controllerLabel}] POST /api/People/New status={(int)resp.StatusCode}", status: (int)resp.StatusCode);
            var retryDoc = await ReadJsonAsync(resp, ct);
            return Unwrap(retryDoc, "/api/People/New");
        }
    }

    // ------------------------------------------------------------------ //
    // Helpers
    // ------------------------------------------------------------------ //

    private static readonly JsonSerializerOptions CompactJson = new() { WriteIndented = false };

    /// <summary>
    /// Hash da senha no login (algoritmo do JS do painel): <c>MD5(Hash + senha + Hash)</c> em hex
    /// MAIÚSCULO, onde <paramref name="clientHash"/> é o GUID reenviado no corpo. Exposto para teste.
    /// </summary>
    public static string ComputeLoginPasswordHash(string clientHash, string password) =>
        Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(clientHash + password + clientHash)));

    /// <summary>
    /// Monta o corpo multipart/form-data do /api/People/New, réplica byte-a-byte do que o browser do
    /// painel envia. Detalhes que o firmware exige:
    /// <list type="bullet">
    ///   <item>A parte <c>PeopleJson</c> é campo simples SEM header Content-Type. Se o .NET adicionar
    ///     <c>application/json</c> (o MultipartFormDataContent adiciona por padrão), o aparelho fecha a
    ///     conexão sem responder — por isso montamos o corpo à mão.</item>
    ///   <item>A parte <c>Photo</c> usa <c>Content-Type: image/jpg</c> (NÃO <c>image/jpeg</c>).</item>
    ///   <item>Quebra de linha CRLF; boundary prefixado com <c>----</c> (convenção WebKit).</item>
    /// </list>
    /// </summary>
    internal static (byte[] body, string contentType) BuildMultipartBody(string peopleJsonStr, byte[]? photoBytes, string photoFilename)
    {
        var boundary = "----facial" + Guid.NewGuid().ToString("N")[..24];
        var bdy = Encoding.ASCII.GetBytes($"--{boundary}");
        var end = Encoding.ASCII.GetBytes($"--{boundary}--");
        var crlf = "\r\n"u8.ToArray();

        using var ms = new MemoryStream();
        void Write(byte[] b) => ms.Write(b, 0, b.Length);
        void WriteAscii(string s) => Write(Encoding.ASCII.GetBytes(s));

        if (photoBytes is { Length: > 0 })
        {
            Write(bdy); Write(crlf);
            WriteAscii($"Content-Disposition: form-data; name=\"Photo\"; filename=\"{photoFilename}\""); Write(crlf);
            WriteAscii("Content-Type: image/jpg"); Write(crlf); Write(crlf);
            Write(photoBytes); Write(crlf);
        }

        Write(bdy); Write(crlf);
        WriteAscii("Content-Disposition: form-data; name=\"PeopleJson\""); Write(crlf); Write(crlf);
        Write(Encoding.UTF8.GetBytes(peopleJsonStr)); Write(crlf);
        Write(end); Write(crlf);

        return (ms.ToArray(), $"multipart/form-data; boundary={boundary}");
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
        if (bytes.Length == 0) return JsonDocument.Parse("{}");
        try { return JsonDocument.Parse(bytes); }
        catch (JsonException ex)
        {
            var preview = Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 200));
            throw new DeviceHttpException($"resposta não-JSON: {ex.Message} | {preview}", inner: ex);
        }
    }

    /// <summary>Desembrulha {result,content,errCode,error}. Lança em result:false; devolve content em result:true.</summary>
    private JsonElement Unwrap(JsonDocument doc, string path)
    {
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return root.Clone();
        if (root.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.False)
        {
            var (code, msg) = ExtractError(root);
            throw new DeviceHttpException($"[{_controllerLabel}] POST {path} errCode={code} ({(code is int c ? DeviceErrorCodes.DescribeError(c) : "?")}) error={msg}", status: 200, errCode: code);
        }
        if (root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Object)
            return content.Clone();
        return root.Clone();
    }

    private static (int? code, string msg) ExtractError(JsonElement root)
    {
        int? code = root.TryGetProperty("errCode", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : null;
        string msg = root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() ?? "unknown" : "unknown";
        return (code, msg);
    }

    /// <summary>Procura o token recursivamente. FC-8190H: <c>content.token</c>.</summary>
    private static string? ExtractToken(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        foreach (var key in new[] { "token", "Token", "AccessToken", "access_token", "BearerToken", "bearer_token" })
            if (el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(v.GetString()))
                return v.GetString();
        foreach (var nk in new[] { "content", "Content", "Data", "data", "Result", "result" })
            if (el.TryGetProperty(nk, out var nested) && nested.ValueKind == JsonValueKind.Object)
            {
                var t = ExtractToken(nested);
                if (t != null) return t;
            }
        return null;
    }

    /// <summary>Lê o campo QRCode de um GetDetail (tolerante a variações de capitalização).</summary>
    public static string? ExtractQrCode(JsonElement detail)
    {
        foreach (var field in new[] { "QRCode", "QrCode", "qrcode", "qr_code", "QRcode", "Qrcode" })
            if (detail.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(v.GetString()))
                return v.GetString();
        return null;
    }
}
