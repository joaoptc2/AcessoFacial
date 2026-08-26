using HospitalAccess.Api.Services;
using Xunit;

namespace HospitalAccess.Tests.Access;

/// <summary>
/// Validação do nome que vai para o aparelho. Antes não havia nenhuma: nome vazio ou gigante era
/// gravado, entrava na fila e só falhava no hardware, com mensagem que não apontava a causa.
/// </summary>
public class PersonNameRulesTests
{
    private const char Lf = (char)10;
    private const char Cr = (char)13;

    [Fact]
    public void Aceita_NomeComum()
    {
        Assert.True(PersonNameRules.TryNormalize("Maria da Silva", "O nome", out var nome, out var erro));
        Assert.Equal("Maria da Silva", nome);
        Assert.Null(erro);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Recusa_NomeVazioOuSoEspaco(string? entrada)
    {
        Assert.False(PersonNameRules.TryNormalize(entrada, "O nome", out _, out var erro));
        Assert.Contains("obrigatório", erro);
    }

    [Fact]
    public void Recusa_NomeCurtoDemais()
    {
        Assert.False(PersonNameRules.TryNormalize("A", "O nome", out _, out var erro));
        Assert.Contains("pelo menos", erro);
    }

    [Fact]
    public void Recusa_NomeAcimaDoLimite_ComTamanhoNaMensagem()
    {
        var longo = new string('a', PersonNameRules.MaxLength + 1);

        Assert.False(PersonNameRules.TryNormalize(longo, "O nome", out _, out var erro));
        Assert.Contains($"{PersonNameRules.MaxLength}", erro);
        // A mensagem diz o tamanho recebido: quem cadastrou sabe quanto precisa cortar.
        Assert.Contains($"{PersonNameRules.MaxLength + 1}", erro);
    }

    [Fact]
    public void Aceita_ExatamenteNoLimite()
    {
        var noLimite = new string('a', PersonNameRules.MaxLength);
        Assert.True(PersonNameRules.TryNormalize(noLimite, "O nome", out var nome, out _));
        Assert.Equal(PersonNameRules.MaxLength, nome.Length);
    }

    [Fact]
    public void Normaliza_EspacoNasPontasEDuplicadoNoMeio()
    {
        // Sem isto, "João  Silva" e "João Silva" viram dois cadastros visualmente idênticos.
        Assert.True(PersonNameRules.TryNormalize("  João   Pedro  Silva ", "O nome", out var nome, out _));
        Assert.Equal("João Pedro Silva", nome);
    }

    [Fact]
    public void Recusa_QuebraDeLinhaNoMeioDoNome()
    {
        // Quebra de linha dentro do nome quebra a serialização do protocolo e não tem uso legítimo.
        var comQuebra = "Maria" + Lf + "Silva";

        Assert.False(PersonNameRules.TryNormalize(comQuebra, "O nome", out _, out var erro));
        Assert.Contains("inválidos", erro);
    }

    [Fact]
    public void Limpa_QuebraDeLinhaNasPontas()
    {
        // Colar de uma planilha costuma trazer CRLF no fim: isso é sujeira, não nome inválido.
        var comSujeira = "Maria da Silva" + Cr + Lf;

        Assert.True(PersonNameRules.TryNormalize(comSujeira, "O nome", out var nome, out var erro));
        Assert.Equal("Maria da Silva", nome);
        Assert.Null(erro);
    }

    [Fact]
    public void Preserva_AcentosECedilha()
    {
        Assert.True(PersonNameRules.TryNormalize("Conceição Assunção", "O nome", out var nome, out _));
        Assert.Equal("Conceição Assunção", nome);
    }

    [Fact]
    public void UsaORotuloRecebidoNaMensagem()
    {
        Assert.False(PersonNameRules.TryNormalize("", "O nome do paciente", out _, out var erro));
        Assert.StartsWith("O nome do paciente", erro);
    }
}
