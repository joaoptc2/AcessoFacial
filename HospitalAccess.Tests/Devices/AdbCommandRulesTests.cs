using HospitalAccess.Api.Services;
using Xunit;

namespace HospitalAccess.Tests.Devices;

/// <summary>
/// Regras do canal ADB com os sticks de TV dos quartos. O que está em jogo: tudo que passa daqui
/// é executado pelo SHELL DO APARELHO. Uma validação frouxa nestas funções transforma o painel
/// de manutenção numa forma de rodar comando arbitrário em 30 aparelhos do hospital.
/// </summary>
public class AdbCommandRulesTests
{
    // ---------------------------------------------------------------- endereço

    [Theory]
    [InlineData("192.168.19.11", 5555, "192.168.19.11:5555")]
    [InlineData("  192.168.19.11  ", 5555, "192.168.19.11:5555")]  // espaço no cadastro
    [InlineData("2001:db8::1", 5555, "[2001:db8::1]:5555")]        // IPv6 exige colchetes
    public void EnderecoValido_EhMontado(string ip, int porta, string esperado)
    {
        Assert.True(AdbCommandRules.TryBuildDeviceAddress(ip, porta, out var address, out var erro));
        Assert.Equal(esperado, address);
        Assert.Null(erro);
    }

    [Theory]
    [InlineData(null, 5555)]
    [InlineData("", 5555)]
    [InlineData("nao-e-ip", 5555)]
    [InlineData("192.168.19.11", 0)]
    [InlineData("192.168.19.11", 70000)]
    public void EnderecoInvalido_NaoChegaAoAdb(string? ip, int porta)
    {
        Assert.False(AdbCommandRules.TryBuildDeviceAddress(ip, porta, out var address, out var erro));
        Assert.Equal(string.Empty, address);
        Assert.False(string.IsNullOrWhiteSpace(erro));
    }

    // ---------------------------------------------------------------- pacotes

    [Theory]
    [InlineData("com.globo.globotv")]
    [InlineData("tv.pluto.android")]
    [InlineData("br.com.skymais")]
    [InlineData("com.spocky.projengmenu")]
    [InlineData("com.android.tv.settings")]
    public void PacotesReaisDoAparelho_SaoAceitos(string pacote) =>
        Assert.True(AdbCommandRules.IsValidPackageName(pacote));

    /// <summary>
    /// Cada entrada é uma forma de virar dois comandos no aparelho. O conjunto de caracteres
    /// aceito exclui todos os metacaracteres do shell — por isso um pacote aprovado pode ir no
    /// comando sem aspas.
    /// </summary>
    [Theory]
    [InlineData("com.x;reboot")]
    [InlineData("com.x && rm -rf /")]
    [InlineData("com.x|nc 10.0.0.1 1234")]
    [InlineData("com.x$(id)")]
    [InlineData("com.x`id`")]
    [InlineData("com.x\nreboot")]
    [InlineData("../../etc/passwd")]
    [InlineData("sem-ponto")]          // precisa de pelo menos um ponto
    [InlineData("1com.comeca.com.digito")]
    [InlineData("")]
    [InlineData(null)]
    public void PacoteHostil_EhRecusado(string? pacote) =>
        Assert.False(AdbCommandRules.IsValidPackageName(pacote));

    [Fact]
    public void PacoteLongoDemais_EhRecusado() =>
        Assert.False(AdbCommandRules.IsValidPackageName("com." + new string('a', 300)));

    // ---------------------------------------------------------------- teclas

    [Theory]
    [InlineData("DPAD_DOWN")]
    [InlineData("DPAD_CENTER")]
    [InlineData("BACK")]
    [InlineData("VOLUME_UP")]
    public void TeclaDaLista_EhAceita(string tecla) => Assert.True(AdbCommandRules.IsAllowedKey(tecla));

    [Theory]
    [InlineData("DPAD_DOWN; reboot")]
    [InlineData("KEYCODE_INEXISTENTE")]
    [InlineData("dpad_down")]          // lista fechada e sensível a caixa, de propósito
    [InlineData("26")]                 // código numérico não passa: só nomes conhecidos
    [InlineData("")]
    [InlineData(null)]
    public void TeclaForaDaLista_EhRecusada(string? tecla) => Assert.False(AdbCommandRules.IsAllowedKey(tecla));

    // ---------------------------------------------------------------- aspas

    [Fact]
    public void AspasSimples_NeutralizamMetacaracteres()
    {
        var citado = AdbCommandRules.QuoteForDeviceShell("a; reboot $(id) && rm -rf / | nc");
        Assert.Equal("'a; reboot $(id) && rm -rf / | nc'", citado);
    }

    /// <summary>
    /// A aspa simples é o único caractere que escapa de dentro de aspas simples. O padrão
    /// <c>'\''</c> fecha, emite uma literal e reabre — sem isso, uma senha com apóstrofo abriria
    /// o resto da linha para interpretação.
    /// </summary>
    [Fact]
    public void ApostrofoNoTexto_EhFechadoEReaberto()
    {
        var citado = AdbCommandRules.QuoteForDeviceShell("d'agua");
        Assert.Equal(@"'d'\''agua'", citado);
    }

    // ---------------------------------------------------------------- texto digitado

    [Fact]
    public void EspacoViraPorcentoS_QueEhComoOAndroidDigitaEspaco()
    {
        Assert.True(AdbCommandRules.TryEncodeInputText("joao da silva", out var pedacos, out _));
        Assert.Equal(["joao%sda%ssilva"], pedacos);
    }

    /// <summary>
    /// O <c>input text</c> troca TODA ocorrência de "%s" por espaço. Uma senha com "%s" literal
    /// seria corrompida em silêncio — o texto é quebrado para que nenhum pedaço contenha a
    /// sequência inteira, e o literal chega como está.
    /// </summary>
    [Theory]
    [InlineData("ab%scd", new[] { "ab%", "scd" })]
    [InlineData("%s", new[] { "%", "s" })]
    [InlineData("a%s b", new[] { "a%", "s%sb" })]
    [InlineData("100% certo", new[] { "100%", "%scerto" })]
    public void PorcentoSLiteral_EhPreservadoPelaQuebra(string texto, string[] esperado)
    {
        Assert.True(AdbCommandRules.TryEncodeInputText(texto, out var pedacos, out _));
        Assert.Equal(esperado, pedacos);
    }

    /// <summary>
    /// Prova de ida e volta: aplicar em cada pedaço a MESMA transformação que o Android aplica
    /// ("%s" → espaço) e concatenar precisa devolver exatamente o texto original.
    /// </summary>
    [Theory]
    [InlineData("senha simples")]
    [InlineData("ab%scd")]
    [InlineData("100% certo")]
    [InlineData("S3nh@!#$&*()[]{}<>|;`\"\\/")]
    [InlineData("%%%sss%s")]
    public void TextoSobrevivePorInteiro(string original)
    {
        Assert.True(AdbCommandRules.TryEncodeInputText(original, out var pedacos, out _));
        var reconstruido = string.Concat(pedacos.Select(p => p.Replace("%s", " ")));
        Assert.Equal(original, reconstruido);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("acentuação")]     // input text não transmite não-ASCII de forma confiável
    [InlineData("linha1\nlinha2")] // quebra de linha é a tecla ENTER, não texto
    [InlineData("com\ttab")]
    public void TextoImpossivel_EhRecusadoComMotivo(string? texto)
    {
        Assert.False(AdbCommandRules.TryEncodeInputText(texto, out var pedacos, out var erro));
        Assert.Empty(pedacos);
        Assert.False(string.IsNullOrWhiteSpace(erro));
    }

    [Fact]
    public void TextoLongoDemais_EhRecusado() =>
        Assert.False(AdbCommandRules.TryEncodeInputText(new string('a', 513), out _, out _));

    // ---------------------------------------------------------------- leitura das saídas

    [Theory]
    [InlineData("226.18 326.82", 226.18)]
    [InlineData("  48291.44 1000.00  ", 48291.44)]
    [InlineData("", null)]
    [InlineData("sem numero", null)]
    [InlineData(null, null)]
    public void UptimePegaOPrimeiroNumero(string? saida, double? esperado) =>
        Assert.Equal(esperado, AdbCommandRules.ParseUptimeSeconds(saida));

    [Fact]
    public void FocoEhExtraidoDaLinhaDoDumpsys()
    {
        const string saida = "  mCurrentFocus=Window{5fd333c u0 com.android.tv.settings/com.android.tv.settings.MainSettings}";
        var foco = AdbCommandRules.ParseCurrentFocus(saida);

        Assert.Equal("com.android.tv.settings/com.android.tv.settings.MainSettings", foco);
        Assert.Equal("com.android.tv.settings", AdbCommandRules.PackageOf(foco));
    }

    [Theory]
    [InlineData("mCurrentFocus=null")]   // tela apagada
    [InlineData("nenhuma linha relevante")]
    [InlineData("")]
    [InlineData(null)]
    public void SemFoco_DevolveNulo(string? saida) => Assert.Null(AdbCommandRules.ParseCurrentFocus(saida));

    [Fact]
    public void ListaDePacotes_IgnoraRuidoDoDaemon()
    {
        const string saida = """
            * daemon not running; starting now at tcp:5037
            package:com.wbd.stream
            package:tv.pluto.android
            package:/data/app/base.apk=com.globo.globotv
            linha solta
            package:invalido sem ponto
            """;

        var pacotes = AdbCommandRules.ParsePackageList(saida);

        Assert.Equal(["com.wbd.stream", "tv.pluto.android"], pacotes);
    }

    [Theory]
    [InlineData("connected to 192.168.19.11:5555", true)]
    [InlineData("already connected to 192.168.19.11:5555", true)]
    [InlineData("failed to connect to 192.168.19.11:5555", false)]
    [InlineData("cannot connect to 192.168.19.11:5555: Connection refused", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ConexaoEhDecididaPeloTexto_NaoPeloCodigoDeSaida(string? saida, bool esperado) =>
        Assert.Equal(esperado, AdbCommandRules.ParseConnectSucceeded(saida));

    [Fact]
    public void ChaveRecusada_EhDistinguidaDeOffline()
    {
        Assert.True(AdbCommandRules.IsUnauthorized("adb.exe: device unauthorized."));
        Assert.False(AdbCommandRules.IsUnauthorized("error: device '192.168.19.11:5555' not found"));
        Assert.False(AdbCommandRules.IsUnauthorized(null));
    }

    // ---------------------------------------------------------------- seções marcadas

    [Fact]
    public void SecoesMarcadas_SobrevivemASecaoVazia()
    {
        // getprop pode devolver vazio; contar linhas deslocaria tudo dali em diante.
        const string saida = """
            __MODELO__
            __UPTIME__
            226.18 326.82
            __FOCO__
              mCurrentFocus=Window{1 u0 com.x/.Main}
            """;

        var secoes = AdbCommandRules.ParseMarkedSections(saida, "__MODELO__", "__UPTIME__", "__FOCO__");

        Assert.Equal(string.Empty, secoes["__MODELO__"]);
        Assert.Equal("226.18 326.82", secoes["__UPTIME__"]);
        Assert.Contains("mCurrentFocus", secoes["__FOCO__"]);
    }

    [Fact]
    public void SecoesMarcadas_IgnoramLixoAntesDoPrimeiroMarcador()
    {
        var secoes = AdbCommandRules.ParseMarkedSections("aviso do daemon\n__A__\nvalor", "__A__");

        Assert.Single(secoes);
        Assert.Equal("valor", secoes["__A__"]);
    }

    [Fact]
    public void SecoesMarcadas_SaidaVaziaOuSemMarcador_NaoQuebra()
    {
        Assert.Empty(AdbCommandRules.ParseMarkedSections(null, "__A__"));
        Assert.Empty(AdbCommandRules.ParseMarkedSections("qualquer coisa"));
        Assert.Empty(AdbCommandRules.ParseMarkedSections("sem marcador aqui", "__A__"));
    }
}
