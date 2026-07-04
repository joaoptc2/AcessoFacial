using HospitalAccess.Domain.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HospitalAccess.Infrastructure.Devices;

/// <summary>
/// Cria <see cref="DeviceHttpClient"/> por controlador. Compartilha um único
/// <see cref="SocketsHttpHandler"/> (pool de conexões) que IGNORA validação de certificado — os
/// aparelhos usam cert auto-assinado ou HTTP puro na rede local. O bypass é ESCOPADO a este handler
/// (só as chamadas aos controladores), nunca global.
/// </summary>
public sealed class DeviceHttpClientFactory : IDisposable
{
    private readonly SocketsHttpHandler _handler;
    private readonly DeviceHttpOptions _options;
    private readonly ILogger<DeviceHttpClient> _logger;

    public DeviceHttpClientFactory(IOptions<DeviceHttpOptions> options, ILogger<DeviceHttpClient> logger)
    {
        _options = options.Value;
        _logger = logger;
        _handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            SslOptions = new System.Net.Security.SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = static (_, _, _, _) => true,
            },
        };
    }

    /// <summary>Senha efetiva do painel web: a do controlador se preenchida, senão o padrão global.</summary>
    public string? ResolvePassword(Controller controller) =>
        string.IsNullOrEmpty(controller.ApiPassword) ? _options.DefaultApiPassword : controller.ApiPassword;

    /// <summary>True se o controlador está apto a falar HTTP (tem URL base e uma senha resolvida).</summary>
    public bool CanUseHttp(Controller controller) =>
        !string.IsNullOrWhiteSpace(controller.ApiBaseUrl) && !string.IsNullOrEmpty(ResolvePassword(controller));

    /// <summary>Cria o client para um controlador. Lança se faltar URL base.</summary>
    public DeviceHttpClient Create(Controller controller)
    {
        if (string.IsNullOrWhiteSpace(controller.ApiBaseUrl))
            throw new DeviceHttpException($"Controlador '{controller.Name}' sem ApiBaseUrl configurada.");

        var http = new HttpClient(_handler, disposeHandler: false)
        {
            BaseAddress = new Uri(controller.ApiBaseUrl.TrimEnd('/')),
            Timeout = TimeSpan.FromMilliseconds(_options.TimeoutMs),
        };
        http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "*/*");
        http.DefaultRequestHeaders.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "HospitalAccess-backend/1.0");

        return new DeviceHttpClient(http, _options.LoginPath, ResolvePassword(controller) ?? "", controller.ApiToken, controller.Name, _logger);
    }

    public void Dispose() => _handler.Dispose();
}
