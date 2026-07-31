using HospitalAccess.Api.Services;
using HospitalAccess.Gateway;
using Xunit;

namespace HospitalAccess.Tests.Devices;

public class RelocateRulesTests
{
    private const string Sn = "0000000000000001";

    [Fact]
    public void Sn_ausente_da_varredura_e_not_found()
    {
        var found = new[] { new DiscoveredController("9999999999999999", "192.168.19.30", "AA-BB-CC-DD-EE-FF") };
        var result = RelocateRules.Match(Sn, "192.168.19.21", found);
        Assert.Equal(RelocateRules.Outcome.NotFound, result.Outcome);
        Assert.Null(result.DiscoveredIp);
    }

    [Fact]
    public void Varredura_vazia_e_not_found()
    {
        var result = RelocateRules.Match(Sn, "192.168.19.21", []);
        Assert.Equal(RelocateRules.Outcome.NotFound, result.Outcome);
    }

    [Fact]
    public void Resposta_sem_ip_nao_conta_como_encontrado()
    {
        var found = new[] { new DiscoveredController(Sn, "", "AA-BB-CC-DD-EE-FF") };
        var result = RelocateRules.Match(Sn, "192.168.19.21", found);
        Assert.Equal(RelocateRules.Outcome.NotFound, result.Outcome);
    }

    [Fact]
    public void Mesmo_ip_cadastrado_e_same_ip()
    {
        var found = new[] { new DiscoveredController(Sn, "192.168.19.21", "AA-BB-CC-DD-EE-FF") };
        var result = RelocateRules.Match(Sn, "192.168.19.21", found);
        Assert.Equal(RelocateRules.Outcome.SameIp, result.Outcome);
        Assert.Equal("192.168.19.21", result.DiscoveredIp);
    }

    [Fact]
    public void Ip_diferente_e_moved_com_o_ip_novo()
    {
        var found = new[]
        {
            new DiscoveredController("9999999999999999", "192.168.19.30", "11-22-33-44-55-66"),
            new DiscoveredController(Sn, "192.168.19.87", "AA-BB-CC-DD-EE-FF"),
        };
        var result = RelocateRules.Match(Sn, "192.168.19.21", found);
        Assert.Equal(RelocateRules.Outcome.Moved, result.Outcome);
        Assert.Equal("192.168.19.87", result.DiscoveredIp);
    }

    [Fact]
    public void Sn_duplicado_na_lista_o_primeiro_vence()
    {
        var found = new[]
        {
            new DiscoveredController(Sn, "192.168.19.87", "AA-BB-CC-DD-EE-FF"),
            new DiscoveredController(Sn, "192.168.19.88", "AA-BB-CC-DD-EE-00"),
        };
        var result = RelocateRules.Match(Sn, "192.168.19.21", found);
        Assert.Equal("192.168.19.87", result.DiscoveredIp);
    }

    [Theory]
    // Derivada do IP antigo (com ou sem barra final) acompanha o IP novo.
    [InlineData("http://192.168.19.21", "http://192.168.19.87")]
    [InlineData("http://192.168.19.21/", "http://192.168.19.87")]
    // Customizada ou vazia fica como está.
    [InlineData("http://painel.hospital.local", "http://painel.hospital.local")]
    [InlineData("", "")]
    public void Api_base_url_derivada_acompanha_o_ip_novo(string atual, string esperado)
    {
        Assert.Equal(esperado, RelocateRules.FollowDerivedApiBaseUrl(atual, "192.168.19.21", "192.168.19.87"));
    }
}

public class NetworkFieldRulesTests
{
    private static ControllerNetworkInfo Valid() => new(
        Mac: "AA-BB-CC-DD-EE-FF", Ip: "192.168.19.21", IpMask: "255.255.255.0", IpGateway: "192.168.19.1",
        Dns: "8.8.8.8", DnsBackup: "", UdpPort: 8101, ServerIp: "0.0.0.0", ServerAddr: "", ServerPort: 0, AutoIp: false);

    [Fact]
    public void Configuracao_valida_passa()
    {
        Assert.Null(NetworkFieldRules.Validate(Valid()));
    }

    [Theory]
    [InlineData("AA:BB:CC:DD:EE:FF")] // separador errado (o demo oficial exige hífen)
    [InlineData("AA-BB-CC-DD-EE")] // curto
    [InlineData("GG-BB-CC-DD-EE-FF")] // não-hex
    [InlineData("")]
    public void Mac_invalido_e_recusado(string mac)
    {
        Assert.Contains("MAC", NetworkFieldRules.Validate(Valid() with { Mac = mac }));
    }

    [Fact]
    public void Ip_zero_e_recusado_por_ser_reset_de_fabrica()
    {
        Assert.Contains("0.0.0.0", NetworkFieldRules.Validate(Valid() with { Ip = "0.0.0.0" }));
    }

    [Theory]
    [InlineData("999.1.1.1")]
    [InlineData("abc")]
    [InlineData("")]
    public void Ip_invalido_e_recusado(string ip)
    {
        Assert.NotNull(NetworkFieldRules.Validate(Valid() with { Ip = ip }));
    }

    [Fact]
    public void Mascara_e_gateway_invalidos_sao_recusados()
    {
        Assert.NotNull(NetworkFieldRules.Validate(Valid() with { IpMask = "x" }));
        Assert.NotNull(NetworkFieldRules.Validate(Valid() with { IpGateway = "x" }));
    }

    [Fact]
    public void Dns_vazio_e_permitido_mas_invalido_nao()
    {
        Assert.Null(NetworkFieldRules.Validate(Valid() with { Dns = "", DnsBackup = "" }));
        Assert.NotNull(NetworkFieldRules.Validate(Valid() with { Dns = "nope" }));
        Assert.NotNull(NetworkFieldRules.Validate(Valid() with { DnsBackup = "nope" }));
    }
}
