using HospitalAccess.Api.Services;
using Xunit;

namespace HospitalAccess.Tests.Beds;

/// <summary>
/// Um script POR quarto (script.BV_14) em vez de um script único que recebe o quarto no
/// payload. O que estes testes protegem: o quarto certo. Um erro aqui não trava nada — ele
/// acende a tela de boas-vindas COM O NOME DO PACIENTE na TV de outro quarto.
/// </summary>
public class HomeAssistantServiceNameTests
{
    [Fact]
    public void Modelo_por_quarto_monta_o_script_do_numero()
    {
        Assert.Equal("script.BV_14", HomeAssistantServiceName.Resolve("script.BV_{quarto}", "14"));
        Assert.Equal("script.BV_1", HomeAssistantServiceName.Resolve("script.BV_{quarto}", "1"));
    }

    /// <summary>
    /// O HA costuma deixar o entity_id minúsculo, mas quem configura é quem sabe como os
    /// scripts da casa foram criados. Normalizar aqui trocaria um "serviço não encontrado"
    /// claro por um mistério — e quebraria quem de fato tem BV_14 em maiúsculas.
    /// </summary>
    [Fact]
    public void Caixa_do_modelo_e_preservada_letra_por_letra()
    {
        Assert.Equal("script.BV_14", HomeAssistantServiceName.Resolve("script.BV_{quarto}", "14"));
        Assert.Equal("script.bv_14", HomeAssistantServiceName.Resolve("script.bv_{quarto}", "14"));
    }

    /// <summary>O marcador é reconhecido em qualquer caixa; o VALOR do quarto entra intocado.</summary>
    [Fact]
    public void Marcador_aceita_qualquer_caixa_mas_o_valor_entra_como_esta()
    {
        Assert.Equal("script.BV_14a", HomeAssistantServiceName.Resolve("script.BV_{QUARTO}", "14a"));
        Assert.Equal("script.BV_14A", HomeAssistantServiceName.Resolve("script.BV_{quarto}", "14A"));
    }

    /// <summary>Alias em inglês: o payload chama o campo de "room", então os dois nomes servem.</summary>
    [Fact]
    public void Marcador_room_funciona_igual()
    {
        Assert.Equal("script.BV_7", HomeAssistantServiceName.Resolve("script.BV_{room}", "7"));
    }

    /// <summary>
    /// Quem nunca mexeu na configuração continua com UM script para todos os quartos, que
    /// recebe o quarto no payload. Sem marcador = nada a resolver.
    /// </summary>
    [Fact]
    public void Sem_marcador_o_servico_vale_como_esta()
    {
        Assert.Equal("script.boas_vindas_leito",
            HomeAssistantServiceName.Resolve("script.boas_vindas_leito", "14"));
        // E vale mesmo sem quarto nenhum — o script único não depende dele para existir.
        Assert.Equal("script.boas_vindas_leito",
            HomeAssistantServiceName.Resolve("script.boas_vindas_leito", ""));
    }

    /// <summary>
    /// O caso perigoso: modelo por quarto num controlador sem o quarto preenchido. Resolver
    /// para "script.BV_" mandaria lixo ao HA; devolver null deixa quem chama explicar.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Modelo_por_quarto_sem_quarto_nao_resolve(string? room)
    {
        Assert.Null(HomeAssistantServiceName.Resolve("script.BV_{quarto}", room));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Modelo_vazio_nao_resolve(string? template)
    {
        Assert.Null(HomeAssistantServiceName.Resolve(template, "14"));
    }

    /// <summary>
    /// O quarto vai PARAR DENTRO do entity_id, então só vale o que o HA aceita lá: letra,
    /// dígito, sublinhado e hífen. "Quarto 101" viraria "script.BV_Quarto 101" — o HA recusa,
    /// e nas boas-vindas (best-effort) a recusa passaria despercebida: a internação dá certo e
    /// ninguém repara que a TV não acendeu.
    /// </summary>
    [Theory]
    [InlineData("Quarto 101")]   // espaço
    [InlineData("14/A")]         // barra
    [InlineData("quarto.101")]   // ponto criaria um domínio a mais
    [InlineData("14;rm -rf")]    // nada de caractere de comando no nome do serviço
    public void Quarto_que_nao_cabe_num_entity_id_nao_resolve(string room)
    {
        Assert.Null(HomeAssistantServiceName.Resolve("script.BV_{quarto}", room));
    }

    [Theory]
    [InlineData("14")]
    [InlineData("1")]
    [InlineData("14A")]
    [InlineData("ala_norte_3")]
    [InlineData("3-b")]
    public void Quarto_com_forma_valida_resolve(string room)
    {
        Assert.Equal($"script.BV_{room}", HomeAssistantServiceName.Resolve("script.BV_{quarto}", room));
    }

    /// <summary>Sem domínio ("BV_14", sem ponto) o HA não tem o que chamar.</summary>
    [Fact]
    public void Modelo_sem_dominio_nao_resolve()
    {
        Assert.Null(HomeAssistantServiceName.Resolve("BV_{quarto}", "14"));
    }

    [Fact]
    public void IsPerRoom_distingue_os_dois_modelos()
    {
        Assert.True(HomeAssistantServiceName.IsPerRoom("script.BV_{quarto}"));
        Assert.True(HomeAssistantServiceName.IsPerRoom("script.BV_{room}"));
        Assert.False(HomeAssistantServiceName.IsPerRoom("script.boas_vindas_leito"));
        Assert.False(HomeAssistantServiceName.IsPerRoom(""));
        Assert.False(HomeAssistantServiceName.IsPerRoom(null));
    }
}
