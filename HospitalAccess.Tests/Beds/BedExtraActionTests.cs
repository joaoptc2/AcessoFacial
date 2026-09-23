using System.Text.Json;
using HospitalAccess.Api.Options;
using HospitalAccess.Api.Services;
using HospitalAccess.Infrastructure.Devices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace HospitalAccess.Tests.Beds;

/// <summary>
/// Ação extra do quarto (ex.: abrir o frigobar): um serviço do Home Assistant à escolha do
/// hospital, que vira um botão na gestão de leitos. O que importa garantir é que o botão só
/// existe quando HÁ serviço configurado — um botão que aparece sem destino é pior que botão
/// nenhum, porque o operador conclui que a automação está quebrada.
/// </summary>
public class BedExtraActionTests
{
    /// <summary>Sem banco: o provider cai no fallback do appsettings (mesmo truque dos testes de boas-vindas).</summary>
    private sealed class NoDbScopeFactory : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => throw new InvalidOperationException("sem banco nos testes");
    }

    private static RuntimeSettingsProvider CreateProvider(HomeAssistantOptions ha) =>
        new(new NoDbScopeFactory(),
            Options.Create(ha),
            Options.Create(new BedManagementOptions()),
            Options.Create(new DeviceHttpOptions()),
            Options.Create(new BackupOptions()),
            NullLogger<RuntimeSettingsProvider>.Instance);

    [Fact]
    public void Room_serializa_somente_o_quarto()
    {
        var payload = HomeAssistantPayload.Room("quarto_101");

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        Assert.Equal("quarto_101", doc.RootElement.GetProperty("room").GetString());
        Assert.Single(doc.RootElement.EnumerateObject());
    }

    [Fact]
    public void Sem_servico_configurado_nao_ha_acao_extra()
    {
        // Padrão de fábrica: rótulo pronto, serviço vazio — nada de botão.
        var settings = CreateProvider(new HomeAssistantOptions()).HomeAssistant;

        Assert.False(settings.HasExtraAction);
        Assert.Equal("Abrir frigobar", settings.ExtraActionLabel);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Servico_em_branco_nao_ha_acao_extra(string service)
    {
        var settings = CreateProvider(new HomeAssistantOptions { ExtraActionService = service }).HomeAssistant;

        Assert.False(settings.HasExtraAction);
    }

    [Fact]
    public void Servico_configurado_habilita_a_acao_extra_com_o_rotulo_do_appsettings()
    {
        var settings = CreateProvider(new HomeAssistantOptions
        {
            ExtraActionService = "script.abrir_frigobar",
            ExtraActionLabel = "Liberar frigobar",
        }).HomeAssistant;

        Assert.True(settings.HasExtraAction);
        Assert.Equal("script.abrir_frigobar", settings.ExtraActionService);
        Assert.Equal("Liberar frigobar", settings.ExtraActionLabel);
    }

    /// <summary>
    /// O rótulo NUNCA fica vazio: um appsettings com o rótulo em branco ainda tem que render um
    /// botão legível, porque o texto do botão é a única pista do que ele faz.
    /// </summary>
    [Fact]
    public void Rotulo_em_branco_no_appsettings_nao_derruba_o_padrao()
    {
        var settings = CreateProvider(new HomeAssistantOptions
        {
            ExtraActionService = "script.abrir_frigobar",
            ExtraActionLabel = "   ",
        }).HomeAssistant;

        Assert.True(settings.HasExtraAction);
        Assert.False(string.IsNullOrWhiteSpace(settings.ExtraActionLabel));
    }
}
