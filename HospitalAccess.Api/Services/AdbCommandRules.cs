using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace HospitalAccess.Api.Services;

/// <summary>
/// Regras puras do canal ADB com os sticks de TV dos quartos: validação do que pode virar
/// comando e leitura das saídas do aparelho. Sem I/O — o processo em si fica no
/// <see cref="AdbClient"/>, e é esta separação que permite testar a parte perigosa.
///
/// <para>
/// POR QUE ISTO É CRÍTICO: tudo que passa de <c>adb shell</c> em diante é executado pelo shell
/// DO APARELHO. Os argumentos são enviados sem shell do nosso lado (<c>ArgumentList</c>), mas o
/// adb os junta numa única linha de comando no destino — então um nome de pacote com
/// <c>; reboot</c> no meio viraria dois comandos lá. Nada daqui vai para o aparelho sem passar
/// por <see cref="IsValidPackageName"/>, <see cref="IsAllowedKey"/> ou
/// <see cref="QuoteForDeviceShell"/>.
/// </para>
/// </summary>
public static partial class AdbCommandRules
{
    // ------------------------------------------------------------------ endereço do aparelho

    /// <summary>
    /// Monta o endereço que o adb usa como identificador do aparelho ("192.168.19.11:5555").
    /// IPv6 sai entre colchetes. False = cadastro inválido (não chega a chamar o adb).
    /// </summary>
    public static bool TryBuildDeviceAddress(string? ip, int port, out string address, out string? error)
    {
        address = string.Empty;
        error = null;

        if (string.IsNullOrWhiteSpace(ip))
        {
            error = "Este leito não tem TV cadastrada.";
            return false;
        }

        if (!IPAddress.TryParse(ip.Trim(), out var parsed))
        {
            error = $"IP da TV inválido: '{ip}'.";
            return false;
        }

        if (port is < 1 or > 65535)
        {
            error = $"Porta da TV inválida: {port}.";
            return false;
        }

        address = parsed.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            ? $"[{parsed}]:{port}"
            : $"{parsed}:{port}";
        return true;
    }

    // ------------------------------------------------------------------ pacotes

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]*(\.[A-Za-z0-9_]+)+$")]
    private static partial Regex PackageNamePattern();

    /// <summary>
    /// Nome de pacote Android válido: segmentos alfanuméricos separados por ponto, começando por
    /// letra, com pelo menos um ponto. O conjunto de caracteres exclui por construção tudo que o
    /// shell do aparelho interpretaria (<c>; | &amp; $ ` ( ) &lt; &gt;</c> e espaço), então um
    /// pacote aprovado aqui pode ir direto no comando sem aspas.
    /// </summary>
    public static bool IsValidPackageName(string? package) =>
        !string.IsNullOrWhiteSpace(package)
        && package.Length <= 255
        && PackageNamePattern().IsMatch(package);

    // ------------------------------------------------------------------ teclas

    /// <summary>
    /// Teclas aceitas pelo painel — lista fechada, não validação por formato. O que não está
    /// aqui não vai para o aparelho. Cobre o controle remoto de uma TV: direcional, confirmação,
    /// navegação, edição de texto, volume e mídia.
    /// </summary>
    public static readonly IReadOnlySet<string> AllowedKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        // direcional e confirmação
        "DPAD_UP", "DPAD_DOWN", "DPAD_LEFT", "DPAD_RIGHT", "DPAD_CENTER", "ENTER",
        // navegação
        "BACK", "HOME", "MENU", "SEARCH", "INFO", "GUIDE", "TV",
        // edição de texto (campos de login)
        "DEL", "FORWARD_DEL", "TAB", "SPACE", "ESCAPE", "MOVE_HOME", "MOVE_END",
        "PAGE_UP", "PAGE_DOWN",
        // volume e energia
        "VOLUME_UP", "VOLUME_DOWN", "VOLUME_MUTE", "POWER",
        // mídia
        "MEDIA_PLAY_PAUSE", "MEDIA_PLAY", "MEDIA_PAUSE", "MEDIA_STOP",
        "MEDIA_NEXT", "MEDIA_PREVIOUS", "MEDIA_REWIND", "MEDIA_FAST_FORWARD",
    };

    /// <summary>Tecla está na lista fechada? Comparação exata — sem normalizar maiúsculas de propósito.</summary>
    public static bool IsAllowedKey(string? key) => key is not null && AllowedKeys.Contains(key);

    // ------------------------------------------------------------------ texto

    /// <summary>
    /// Aspas simples para o shell do APARELHO. Uma aspa simples dentro do valor vira
    /// <c>'\''</c> (fecha, escapa uma literal, reabre) — a única forma segura, porque dentro de
    /// aspas simples o shell não interpreta absolutamente nada, inclusive <c>$</c> e barra.
    /// </summary>
    public static string QuoteForDeviceShell(string value)
    {
        var sb = new StringBuilder(value.Length + 2);
        sb.Append('\'');
        foreach (var c in value)
        {
            if (c == '\'') sb.Append("'\\''");
            else sb.Append(c);
        }
        sb.Append('\'');
        return sb.ToString();
    }

    /// <summary>
    /// Prepara um texto para o <c>input text</c> e devolve os PEDAÇOS a enviar, em ordem.
    ///
    /// <para>
    /// Dois detalhes do Android estão embutidos aqui. Primeiro: o <c>input text</c> troca toda
    /// ocorrência de <c>%s</c> por espaço — é assim que se digita espaço por ele. Segundo, e é a
    /// consequência incômoda: um <c>%s</c> LITERAL (possível numa senha) seria transformado em
    /// espaço e o operador nunca saberia por que o login falhou. Por isso o texto é quebrado logo
    /// depois de cada <c>%</c> seguido de <c>s</c>: nenhum pedaço contém a sequência inteira, e o
    /// literal chega intacto. O caso normal devolve um pedaço só.
    /// </para>
    /// </summary>
    public static bool TryEncodeInputText(string? text, out IReadOnlyList<string> chunks, out string? error)
    {
        chunks = [];
        error = null;

        if (string.IsNullOrEmpty(text))
        {
            error = "Texto vazio.";
            return false;
        }

        if (text.Length > 512)
        {
            error = "Texto longo demais (máximo 512 caracteres).";
            return false;
        }

        foreach (var c in text)
        {
            if (c > 127)
            {
                error = $"O caractere '{c}' não é ASCII e o comando de digitação do Android não o "
                      + "transmite de forma confiável. Use o link do scrcpy para digitá-lo.";
                return false;
            }
            if (char.IsControl(c))
            {
                error = "O texto contém caracteres de controle (quebra de linha, tabulação). "
                      + "Use as teclas ENTER e TAB do painel.";
                return false;
            }
        }

        var parts = new List<string>();
        var atual = new StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            atual.Append(text[i] == ' ' ? "%s" : text[i].ToString());

            // Corta logo após um '%' que seria colado a um 's' — inclusive o 's' que acabamos de
            // gerar para um espaço, que também formaria "%s" indevido com um '%' anterior.
            var proximo = i + 1 < text.Length ? text[i + 1] : '\0';
            if (text[i] == '%' && (proximo == 's' || proximo == ' '))
            {
                parts.Add(atual.ToString());
                atual.Clear();
            }
        }
        if (atual.Length > 0) parts.Add(atual.ToString());

        chunks = parts;
        return true;
    }

    // ------------------------------------------------------------------ leitura das saídas

    /// <summary>
    /// Segundos desde o boot, do <c>/proc/uptime</c> ("226.18 326.82" → 226.18). Null se a saída
    /// não tem o formato esperado. Serve para saber que um quarto reiniciou sozinho.
    /// </summary>
    public static double? ParseUptimeSeconds(string? procUptime)
    {
        if (string.IsNullOrWhiteSpace(procUptime)) return null;
        var first = procUptime.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (first is null) return null;
        return double.TryParse(first, NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s : null;
    }

    /// <summary>
    /// Componente em foco a partir do <c>dumpsys window</c>:
    /// <c>mCurrentFocus=Window{5fd333c u0 com.android.tv.settings/...MainSettings}</c> →
    /// <c>com.android.tv.settings/...MainSettings</c>. Null quando não há foco (tela apagada) ou
    /// a linha não aparece.
    /// </summary>
    public static string? ParseCurrentFocus(string? dumpsysWindow)
    {
        if (string.IsNullOrWhiteSpace(dumpsysWindow)) return null;

        foreach (var line in dumpsysWindow.Split('\n'))
        {
            var idx = line.IndexOf("mCurrentFocus=", StringComparison.Ordinal);
            if (idx < 0) continue;

            var resto = line[(idx + "mCurrentFocus=".Length)..].Trim().TrimEnd('}');
            if (resto is "null" or "") return null;

            // O último token com barra é o "pacote/atividade"; os anteriores são id e usuário.
            var token = resto.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                             .LastOrDefault(t => t.Contains('/'));
            return string.IsNullOrWhiteSpace(token) ? null : token;
        }
        return null;
    }

    /// <summary>Pacote de um componente "pacote/atividade". Devolve a entrada se não houver barra.</summary>
    public static string? PackageOf(string? component)
    {
        if (string.IsNullOrWhiteSpace(component)) return null;
        var slash = component.IndexOf('/');
        return slash <= 0 ? component : component[..slash];
    }

    /// <summary>
    /// Pacotes a partir do <c>pm list packages</c> ("package:com.wbd.stream" → "com.wbd.stream").
    /// Linhas que não casam com um nome válido são descartadas em silêncio — a saída do adb
    /// costuma trazer avisos do daemon misturados.
    /// </summary>
    public static IReadOnlyList<string> ParsePackageList(string? pmListOutput)
    {
        if (string.IsNullOrWhiteSpace(pmListOutput)) return [];

        var pacotes = new List<string>();
        foreach (var raw in pmListOutput.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("package:", StringComparison.Ordinal)) continue;

            // Algumas variantes anexam o caminho do APK depois de '=' — fica só o nome.
            var nome = line["package:".Length..].Split('=')[0].Trim();
            if (IsValidPackageName(nome)) pacotes.Add(nome);
        }
        return pacotes;
    }

    /// <summary>
    /// Separa uma saída composta em seções, usando marcadores que o próprio comando imprimiu
    /// (<c>echo __U__; cat /proc/uptime; echo __F__; ...</c>). Existe para o status caber num
    /// único ida-e-volta com o aparelho, em vez de três — e um marcador é mais confiável que
    /// contar linhas, porque um <c>getprop</c> vazio deslocaria todas.
    /// Marcadores ausentes simplesmente não aparecem no resultado.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ParseMarkedSections(string? output, params string[] markers)
    {
        var secoes = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(output) || markers.Length == 0) return secoes;

        string? atual = null;
        var conteudo = new StringBuilder();

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var marcador = markers.FirstOrDefault(m => line.Trim() == m);
            if (marcador is not null)
            {
                if (atual is not null) secoes[atual] = conteudo.ToString().Trim();
                atual = marcador;
                conteudo.Clear();
                continue;
            }
            if (atual is not null) conteudo.Append(line).Append('\n');
        }
        if (atual is not null) secoes[atual] = conteudo.ToString().Trim();

        return secoes;
    }

    /// <summary>
    /// O <c>adb connect</c> sai com código 0 mesmo quando falha, então o veredito está no texto.
    /// "connected to ..." e "already connected to ..." são sucesso; o resto não é.
    /// </summary>
    public static bool ParseConnectSucceeded(string? adbConnectOutput)
    {
        if (string.IsNullOrWhiteSpace(adbConnectOutput)) return false;
        var texto = adbConnectOutput.Trim();
        if (texto.Contains("failed to connect", StringComparison.OrdinalIgnoreCase)) return false;
        if (texto.Contains("cannot connect", StringComparison.OrdinalIgnoreCase)) return false;
        return texto.Contains("connected to", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// O aparelho respondeu mas recusou a chave deste servidor. Vale distinguir de "offline"
    /// porque a correção é o oposto: autorizar a chave no aparelho, não conferir a rede.
    ///
    /// <para>
    /// São DUAS frases diferentes, e faltar uma é o que faz o painel dar a instrução errada:
    /// o <c>connect</c> recusado diz "failed to authenticate to &lt;host&gt;", enquanto um comando
    /// contra um transporte já recusado diz "device unauthorized". Só a segunda era reconhecida,
    /// então um servidor sem chave autorizada aparecia como "a TV não respondeu — verifique se
    /// está ligada e na rede", mandando procurar o problema no lugar errado.
    /// </para>
    /// </summary>
    public static bool IsUnauthorized(string? adbOutput) =>
        adbOutput is not null
        && (adbOutput.Contains("device unauthorized", StringComparison.OrdinalIgnoreCase)
            || adbOutput.Contains("failed to authenticate", StringComparison.OrdinalIgnoreCase));
}
