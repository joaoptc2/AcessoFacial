using System.Net;
using HospitalAccess.Api.Options;
using HospitalAccess.Api.Services;
using Microsoft.Extensions.Options;

namespace HospitalAccess.Api.Middleware;

/// <summary>
/// Substitui o endereço do PROXY pelo IP real do cliente e corrige o esquema (http/https) quando
/// a aplicação está atrás de Nginx e/ou do túnel da Cloudflare.
///
/// <para>
/// Fica no TOPO do pipeline de propósito: rate limiter, trilha de auditoria e o handler de erro
/// precisam enxergar o endereço já corrigido. A decisão de confiar ou não nos cabeçalhos está em
/// <see cref="ClientIpResolver"/>, que é testado isoladamente.
/// </para>
/// <para>
/// Por que não o <c>UseForwardedHeaders</c> do framework: além do <c>X-Forwarded-For</c>,
/// precisamos dar precedência ao <c>CF-Connecting-IP</c> (que a Cloudflare sobrescreve na borda e
/// é o único valor não influenciável pelo cliente quando o acesso vem pelo túnel).
/// </para>
/// </summary>
public sealed class ClientIpMiddleware
{
    private const string CloudflareClientIpHeader = "CF-Connecting-IP";
    private const string ForwardedForHeader = "X-Forwarded-For";
    private const string ForwardedProtoHeader = "X-Forwarded-Proto";

    private readonly RequestDelegate _next;
    private readonly IReadOnlyCollection<IPAddress> _trustedProxies;

    public ClientIpMiddleware(RequestDelegate next, IOptions<NetworkOptions> options)
    {
        _next = next;
        _trustedProxies = ClientIpResolver.ParseTrustedProxies(options.Value.TrustedProxies);
    }

    public Task InvokeAsync(HttpContext context)
    {
        var peer = context.Connection.RemoteIpAddress;

        var resolved = ClientIpResolver.Resolve(
            peer,
            context.Request.Headers[CloudflareClientIpHeader].FirstOrDefault(),
            context.Request.Headers[ForwardedForHeader].FirstOrDefault(),
            _trustedProxies);

        if (resolved is not null) context.Connection.RemoteIpAddress = resolved;

        // Esquema: sem isto, a aplicação se vê em "http" mesmo com o cliente em HTTPS, e qualquer
        // URL absoluta gerada sai com o esquema errado. Só é aceito do proxy confiável — o peer
        // original, não o já reescrito acima.
        if (ClientIpResolver.IsTrustedProxy(peer, _trustedProxies))
        {
            var proto = context.Request.Headers[ForwardedProtoHeader].FirstOrDefault();
            if (proto is "http" or "https") context.Request.Scheme = proto;
        }

        return _next(context);
    }
}
