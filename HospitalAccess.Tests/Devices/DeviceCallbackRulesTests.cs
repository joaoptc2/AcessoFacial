using System.Net;
using HospitalAccess.Api.Services;
using Xunit;

namespace HospitalAccess.Tests.Devices;

/// <summary>
/// Conferência do IP de origem do phone-home. O endpoint é anônimo por limitação do firmware,
/// então esta é a única checagem de procedência que o software consegue fazer sozinho.
/// </summary>
public class DeviceCallbackRulesTests
{
    [Fact]
    public void Match_QuandoIpBateExatamente()
    {
        Assert.True(DeviceCallbackRules.SourceIpMatches(IPAddress.Parse("192.168.19.20"), "192.168.19.20"));
    }

    [Fact]
    public void NaoBate_QuandoIpDiferente()
    {
        Assert.False(DeviceCallbackRules.SourceIpMatches(IPAddress.Parse("192.168.19.99"), "192.168.19.20"));
    }

    /// <summary>
    /// Kestrel entrega IPv4 na forma mapeada em IPv6 quando o socket escuta em dual-stack. Sem a
    /// normalização, TODO evento legítimo de uma rede IPv4 seria contado como divergente — o
    /// aviso viraria ruído e a chave de recusa ficaria inutilizável.
    /// </summary>
    [Fact]
    public void Match_QuandoOrigemVemComoIPv4MapeadoEmIPv6()
    {
        var mapeado = IPAddress.Parse("192.168.19.20").MapToIPv6();
        Assert.True(mapeado.IsIPv4MappedToIPv6); // garante que o caso testado é o real
        Assert.True(DeviceCallbackRules.SourceIpMatches(mapeado, "192.168.19.20"));
    }

    [Fact]
    public void Match_QuandoCadastroTambemEstaMapeado()
    {
        Assert.True(DeviceCallbackRules.SourceIpMatches(IPAddress.Parse("192.168.19.20"), "::ffff:192.168.19.20"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nao-e-um-ip")]
    public void Permissivo_QuandoNaoHaOQueComparar(string? cadastrado)
    {
        // Cadastro incompleto ou inválido não pode virar recusa silenciosa de evento de acesso.
        Assert.True(DeviceCallbackRules.SourceIpMatches(IPAddress.Parse("10.0.0.1"), cadastrado));
    }

    /// <summary>
    /// Pegadinha do .NET, fixada aqui de propósito: <c>IPAddress.TryParse</c> aceita a notação
    /// abreviada e lê "192.168.19" como <c>192.168.0.19</c> — não é um parse inválido. Ou seja,
    /// um IP digitado pela metade no cadastro do controlador NÃO cai no caminho permissivo: ele
    /// vira um endereço diferente e o evento aparece como divergente. É o comportamento desejado
    /// (cadastro errado deve chamar atenção no log), mas é surpreendente o bastante para merecer
    /// um teste que documente.
    /// </summary>
    [Fact]
    public void IpAbreviadoEhExpandido_NaoTratadoComoInvalido()
    {
        Assert.True(IPAddress.TryParse("192.168.19", out var expandido));
        Assert.Equal(IPAddress.Parse("192.168.0.19"), expandido);

        Assert.False(DeviceCallbackRules.SourceIpMatches(IPAddress.Parse("192.168.19.20"), "192.168.19"));
        Assert.True(DeviceCallbackRules.SourceIpMatches(IPAddress.Parse("192.168.0.19"), "192.168.19"));
    }

    [Fact]
    public void Permissivo_QuandoOrigemDesconhecida()
    {
        // Sem RemoteIpAddress (pipe em memória, alguns cenários de teste) não há procedência a negar.
        Assert.True(DeviceCallbackRules.SourceIpMatches(null, "192.168.19.20"));
    }

    [Fact]
    public void Ignora_EspacoEmVoltaDoIpCadastrado()
    {
        Assert.True(DeviceCallbackRules.SourceIpMatches(IPAddress.Parse("192.168.19.20"), "  192.168.19.20  "));
    }

    [Fact]
    public void Match_EmIPv6Puro()
    {
        Assert.True(DeviceCallbackRules.SourceIpMatches(IPAddress.Parse("fd00::5"), "fd00::5"));
        Assert.False(DeviceCallbackRules.SourceIpMatches(IPAddress.Parse("fd00::6"), "fd00::5"));
    }
}
