using System.Net;
using HospitalAccess.Api.Services;
using Xunit;

namespace HospitalAccess.Tests.Devices;

/// <summary>
/// Resolução do IP real do cliente atrás de proxy/túnel. O que está em jogo: este endereço
/// governa o rate limit do login, o rate limit do phone-home e a conferência de procedência dos
/// aparelhos. Confiar num cabeçalho forjado aqui é dar a qualquer um a chave dessas três coisas.
/// </summary>
public class ClientIpResolverTests
{
    private static readonly IReadOnlyCollection<IPAddress> Loopback =
        [IPAddress.Loopback, IPAddress.IPv6Loopback];

    private static IPAddress Ip(string value) => IPAddress.Parse(value);

    // ---------------------------------------------------------------- anti-spoof

    [Fact]
    public void OrigemNaoConfiavel_IgnoraCabecalhos()
    {
        // O cenário de ataque: alguém alcança a aplicação por fora do proxy e manda cabeçalhos
        // forjados para escapar do rate limit ou se passar por um controlador.
        var resolvido = ClientIpResolver.Resolve(
            peer: Ip("203.0.113.9"),
            cfConnectingIp: "1.1.1.1",
            xForwardedFor: "8.8.8.8",
            Loopback);

        Assert.Equal(Ip("203.0.113.9"), resolvido);
    }

    [Fact]
    public void OrigemConfiavel_UsaOCabecalho()
    {
        var resolvido = ClientIpResolver.Resolve(
            peer: IPAddress.Loopback,
            cfConnectingIp: null,
            xForwardedFor: "198.51.100.7",
            Loopback);

        Assert.Equal(Ip("198.51.100.7"), resolvido);
    }

    // ---------------------------------------------------------------- precedência

    [Fact]
    public void CfConnectingIp_TemPrecedenciaSobreXForwardedFor()
    {
        // A Cloudflare SOBRESCREVE o CF-Connecting-IP na borda; o X-Forwarded-For pode ter vindo
        // com sujeira do cliente.
        var resolvido = ClientIpResolver.Resolve(
            peer: IPAddress.Loopback,
            cfConnectingIp: "198.51.100.7",
            xForwardedFor: "10.0.0.99, 127.0.0.1",
            Loopback);

        Assert.Equal(Ip("198.51.100.7"), resolvido);
    }

    [Fact]
    public void CfConnectingIpInvalido_CaiNoXForwardedFor()
    {
        var resolvido = ClientIpResolver.Resolve(
            peer: IPAddress.Loopback,
            cfConnectingIp: "nao-e-um-ip",
            xForwardedFor: "198.51.100.7",
            Loopback);

        Assert.Equal(Ip("198.51.100.7"), resolvido);
    }

    // ---------------------------------------------------------------- cadeia do X-Forwarded-For

    /// <summary>
    /// Cada salto ANEXA à direita, então o último endereço não confiável é o cliente real. Pegar
    /// o PRIMEIRO da lista (o erro comum) seria confiar num valor que o próprio cliente pode ter
    /// enviado antes de chegar ao primeiro proxy.
    /// </summary>
    [Fact]
    public void Cadeia_PegaOUltimoNaoConfiavel_NaoOPrimeiro()
    {
        var resolvido = ClientIpResolver.Resolve(
            peer: IPAddress.Loopback,
            cfConnectingIp: null,
            xForwardedFor: "1.2.3.4, 198.51.100.7, 127.0.0.1",
            Loopback);

        Assert.Equal(Ip("198.51.100.7"), resolvido);
        Assert.NotEqual(Ip("1.2.3.4"), resolvido);
    }

    [Fact]
    public void Cadeia_TodosConfiaveis_CaiNoPeer()
    {
        var resolvido = ClientIpResolver.Resolve(
            peer: IPAddress.Loopback,
            cfConnectingIp: null,
            xForwardedFor: "127.0.0.1, 127.0.0.1",
            Loopback);

        Assert.Equal(IPAddress.Loopback, resolvido);
    }

    [Fact]
    public void SemCabecalhoAlgum_DevolveOPeer()
    {
        var resolvido = ClientIpResolver.Resolve(IPAddress.Loopback, null, null, Loopback);
        Assert.Equal(IPAddress.Loopback, resolvido);
    }

    [Fact]
    public void PeerNulo_DevolveNulo()
    {
        Assert.Null(ClientIpResolver.Resolve(null, "1.1.1.1", "8.8.8.8", Loopback));
    }

    // ---------------------------------------------------------------- formatos

    [Theory]
    [InlineData("198.51.100.7:44321", "198.51.100.7")]   // alguns proxies anexam a porta
    [InlineData("  198.51.100.7  ", "198.51.100.7")]     // espaço em volta
    [InlineData("[2001:db8::1]", "2001:db8::1")]         // IPv6 entre colchetes
    [InlineData("[2001:db8::1]:8443", "2001:db8::1")]    // IPv6 com porta
    [InlineData("2001:db8::1", "2001:db8::1")]           // IPv6 puro
    public void AceitaFormatosUsuaisDoCabecalho(string cabecalho, string esperado)
    {
        var resolvido = ClientIpResolver.Resolve(IPAddress.Loopback, cabecalho, null, Loopback);
        Assert.Equal(Ip(esperado), resolvido);
    }

    /// <summary>
    /// O Kestrel reporta IPv4 na forma mapeada em socket dual-stack. Sem normalizar, o MESMO
    /// cliente contaria como dois IPs distintos no rate limit e nunca casaria com o IP cadastrado
    /// de um controlador.
    /// </summary>
    [Fact]
    public void NormalizaIPv4MapeadoEmIPv6()
    {
        var mapeado = Ip("127.0.0.1").MapToIPv6();
        Assert.True(mapeado.IsIPv4MappedToIPv6);

        // Peer mapeado ainda é reconhecido como loopback confiável...
        var resolvido = ClientIpResolver.Resolve(mapeado, "::ffff:198.51.100.7", null, Loopback);

        // ...e o cabeçalho mapeado volta como IPv4 puro.
        Assert.Equal(Ip("198.51.100.7"), resolvido);
    }

    // ---------------------------------------------------------------- configuração

    [Fact]
    public void ProxiesConfiaveis_VazioCaiEmLoopback()
    {
        var proxies = ClientIpResolver.ParseTrustedProxies(null);

        Assert.Contains(IPAddress.Loopback, proxies);
        Assert.Contains(IPAddress.IPv6Loopback, proxies);
    }

    [Fact]
    public void ProxiesConfiaveis_DescartaEntradasInvalidas()
    {
        var proxies = ClientIpResolver.ParseTrustedProxies(["10.0.0.5", "nao-e-ip", ""]);

        Assert.Single(proxies);
        Assert.Contains(Ip("10.0.0.5"), proxies);
    }

    [Fact]
    public void ProxiesConfiaveis_ConfiguradoSubstituiOPadrao()
    {
        // Com um proxy em OUTRO host configurado, o loopback deixa de ser confiável por omissão.
        var proxies = ClientIpResolver.ParseTrustedProxies(["10.0.0.5"]);

        var resolvido = ClientIpResolver.Resolve(
            peer: IPAddress.Loopback,
            cfConnectingIp: "1.1.1.1",
            xForwardedFor: null,
            proxies);

        Assert.Equal(IPAddress.Loopback, resolvido);
    }

    [Fact]
    public void IsTrustedProxy_ReconheceListaEDescartaNulo()
    {
        Assert.True(ClientIpResolver.IsTrustedProxy(IPAddress.Loopback, Loopback));
        Assert.False(ClientIpResolver.IsTrustedProxy(Ip("203.0.113.9"), Loopback));
        Assert.False(ClientIpResolver.IsTrustedProxy(null, Loopback));
    }
}
