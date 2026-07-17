using System.Text.Json;
using HospitalAccess.Api.Services;
using Xunit;

namespace HospitalAccess.Tests.Beds;

public class HomeAssistantPayloadTests
{
    [Fact]
    public void Welcome_serializa_com_as_chaves_do_contrato_do_ha()
    {
        var payload = HomeAssistantPayload.Welcome("quarto_101", "Maria da Silva", "http://srv/welcome/leito-x.jpg?v=1");

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        var root = doc.RootElement;

        Assert.Equal("quarto_101", root.GetProperty("room").GetString());
        Assert.Equal("Maria da Silva", root.GetProperty("patient_name").GetString());
        Assert.Equal("http://srv/welcome/leito-x.jpg?v=1", root.GetProperty("welcome_image_url").GetString());
        Assert.Equal(3, root.EnumerateObject().Count());
    }

    [Fact]
    public void Clear_serializa_somente_o_quarto()
    {
        var payload = HomeAssistantPayload.Clear("quarto_101");

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        var root = doc.RootElement;

        Assert.Equal("quarto_101", root.GetProperty("room").GetString());
        Assert.Single(root.EnumerateObject());
    }

    [Fact]
    public void ParseService_separa_dominio_e_servico_no_primeiro_ponto()
    {
        Assert.Equal(("script", "boas_vindas_leito"), HomeAssistantPayload.ParseService("script.boas_vindas_leito"));
        // Serviços com ponto no nome: o domínio é sempre o trecho antes do PRIMEIRO ponto.
        Assert.Equal(("script", "tv.quarto"), HomeAssistantPayload.ParseService("script.tv.quarto"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("sem_ponto")]
    [InlineData(".comeca_com_ponto")]
    [InlineData("termina_com_ponto.")]
    public void ParseService_malformado_devolve_null(string service)
    {
        Assert.Null(HomeAssistantPayload.ParseService(service));
    }
}
