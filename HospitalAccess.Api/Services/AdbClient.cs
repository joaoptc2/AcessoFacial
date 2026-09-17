using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using HospitalAccess.Api.Options;
using Microsoft.Extensions.Options;

namespace HospitalAccess.Api.Services;

/// <summary>Saída de um comando adb. <see cref="Output"/> é o que faz sentido mostrar ao operador.</summary>
public sealed record AdbResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Ok => ExitCode == 0;

    /// <summary>StdOut quando há; senão StdErr — o adb reporta erro nos dois, dependendo do caso.</summary>
    public string Output => string.IsNullOrWhiteSpace(StdOut) ? StdErr.Trim() : StdOut.Trim();
}

/// <summary>Estado do canal mais a saída bruta do adb — é ela que diz QUAL das causas ocorreu.</summary>
public sealed record TvReach(TvReachState State, string Detail);

/// <summary>Estado do canal com um stick, para o painel dizer o que fazer em vez de só "erro".</summary>
public enum TvReachState
{
    Online,
    /// <summary>Não respondeu: desligado, fora da rede ou IP errado.</summary>
    Offline,
    /// <summary>Respondeu e recusou a chave deste servidor — falta autorizar na bancada.</summary>
    Unauthorized,
    /// <summary>O binário do adb não está instalado no servidor.</summary>
    AdbMissing,
}

/// <summary>
/// Executa o binário do adb contra os sticks de TV. É o equivalente, para as TVs, do que o
/// gateway é para as controladoras de porta.
///
/// <para>
/// Três decisões que valem explicação:
/// </para>
/// <para>
/// <b>Sem shell.</b> Os argumentos vão por <see cref="ProcessStartInfo.ArgumentList"/>, que não
/// passa por <c>/bin/sh</c> — não existe injeção do lado do servidor. Do lado do APARELHO o adb
/// junta os argumentos numa linha só, e é por isso que tudo que vai para <c>adb shell</c> passa
/// antes por <see cref="AdbCommandRules"/>.
/// </para>
/// <para>
/// <b>Reconexão automática.</b> Quando o stick reinicia, o adb do servidor perde o transporte e o
/// comando seguinte falha com "device not found". Em vez de devolver erro, reconectamos e
/// repetimos uma vez — é o comportamento que o operador espera de um quarto que caiu a energia.
/// </para>
/// <para>
/// <b>Um comando por vez, por aparelho.</b> A tela do painel recarrega a cada segundo e o
/// operador digita ao mesmo tempo; sem serializar, a captura e a tecla disputam o mesmo
/// transporte. Aparelhos diferentes seguem em paralelo.
/// </para>
/// </summary>
public sealed class AdbClient
{
    private readonly TvOptions _options;
    private readonly ILogger<AdbClient> _logger;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _porAparelho = new();

    public AdbClient(IOptions<TvOptions> options, ILogger<AdbClient> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public TimeSpan CommandTimeout => TimeSpan.FromSeconds(Math.Max(1, _options.CommandTimeoutSeconds));
    public TimeSpan ScreenshotTimeout => TimeSpan.FromSeconds(Math.Max(1, _options.ScreenshotTimeoutSeconds));
    public TimeSpan LongCommandTimeout => TimeSpan.FromSeconds(Math.Max(1, _options.LongCommandTimeoutSeconds));

    // ------------------------------------------------------------------ comandos

    /// <summary>
    /// Roda <c>adb -s &lt;endereço&gt; ...</c>, reconectando e repetindo uma vez se o transporte
    /// tiver caído. Os argumentos NÃO passam por shell nenhum do nosso lado.
    /// </summary>
    public async Task<AdbResult> DeviceAsync(
        string address, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
    {
        var gate = _porAparelho.GetOrAdd(address, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var result = await RawAsync([.. Prefixed(address, args)], timeout, ct);
            if (!PrecisaReconectar(result)) return result;

            _logger.LogInformation("TV {Address}: transporte caiu ({Saida}); reconectando.", address, result.Output);
            var conectou = await RawAsync(["connect", address], CommandTimeout, ct);
            if (!AdbCommandRules.ParseConnectSucceeded(conectou.Output)) return result;

            return await RawAsync([.. Prefixed(address, args)], timeout, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Atalho para <c>adb -s &lt;endereço&gt; shell ...</c>.</summary>
    public Task<AdbResult> ShellAsync(
        string address, IReadOnlyList<string> shellArgs, TimeSpan timeout, CancellationToken ct) =>
        DeviceAsync(address, ["shell", .. shellArgs], timeout, ct);

    /// <summary>
    /// PNG da tela. Usa <c>exec-out</c>, que entrega o binário puro na saída padrão — sem escrever
    /// no armazenamento do aparelho e sem o round-trip de <c>pull</c>. Null = não conseguiu.
    /// </summary>
    public async Task<byte[]?> ScreenshotAsync(string address, CancellationToken ct)
    {
        var gate = _porAparelho.GetOrAdd(address, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var png = await RawBinaryAsync(
                [.. Prefixed(address, ["exec-out", "screencap", "-p"])], ScreenshotTimeout, ct);

            if (EhPng(png)) return png;

            // Transporte caído: reconecta e tenta de novo, igual aos demais comandos.
            var conectou = await RawAsync(["connect", address], CommandTimeout, ct);
            if (!AdbCommandRules.ParseConnectSucceeded(conectou.Output)) return null;

            png = await RawBinaryAsync(
                [.. Prefixed(address, ["exec-out", "screencap", "-p"])], ScreenshotTimeout, ct);
            return EhPng(png) ? png : null;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Conecta explicitamente (usado no status, antes de qualquer leitura). Devolve também a
    /// SAÍDA BRUTA do adb: "a TV não respondeu" não diz se falta instalar o adb, se a rota até o
    /// quarto está fechada ou se o aparelho recusou a chave deste servidor — e são correções
    /// completamente diferentes. Quem opera precisa da frase original.
    /// </summary>
    public async Task<TvReach> ConnectAsync(string address, CancellationToken ct)
    {
        var result = await RawAsync(["connect", address], CommandTimeout, ct);
        var detalhe = Detail(result);

        if (result.ExitCode == AdbNaoEncontrado) return new TvReach(TvReachState.AdbMissing, detalhe);
        if (AdbCommandRules.IsUnauthorized(detalhe)) return new TvReach(TvReachState.Unauthorized, detalhe);

        return AdbCommandRules.ParseConnectSucceeded(detalhe)
            ? new TvReach(TvReachState.Online, detalhe)
            : new TvReach(TvReachState.Offline, detalhe);
    }

    /// <summary>
    /// Tudo que o adb escreveu, das duas saídas. O <see cref="AdbResult.Output"/> normal prefere
    /// uma só; aqui interessam as duas juntas, porque o "connect" manda o sucesso para a saída
    /// padrão e a falha para a de erro — e a mensagem que falta é sempre a outra.
    /// </summary>
    public static string Detail(AdbResult result)
    {
        var partes = new[] { result.StdOut, result.StdErr }
            .Select(p => p?.Trim())
            .Where(p => !string.IsNullOrEmpty(p));
        return string.Join(" | ", partes!);
    }

    /// <summary>Classifica a saída de um comando para o painel orientar a correção certa.</summary>
    public static TvReachState Classify(AdbResult result)
    {
        if (result.ExitCode == AdbNaoEncontrado) return TvReachState.AdbMissing;
        if (AdbCommandRules.IsUnauthorized(result.Output)) return TvReachState.Unauthorized;
        return result.Ok ? TvReachState.Online : TvReachState.Offline;
    }

    // ------------------------------------------------------------------ processo

    /// <summary>Código sintético: o binário do adb não existe no servidor.</summary>
    public const int AdbNaoEncontrado = -127;

    private static IEnumerable<string> Prefixed(string address, IReadOnlyList<string> args)
    {
        yield return "-s";
        yield return address;
        foreach (var a in args) yield return a;
    }

    private static bool PrecisaReconectar(AdbResult r) =>
        !r.Ok
        && (r.Output.Contains("not found", StringComparison.OrdinalIgnoreCase)
            || r.Output.Contains("device offline", StringComparison.OrdinalIgnoreCase)
            || r.Output.Contains("no devices", StringComparison.OrdinalIgnoreCase));

    private static bool EhPng(byte[]? data) =>
        data is { Length: > 8 } && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47;

    private ProcessStartInfo Info(IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _options.AdbPath,
            UseShellExecute = false,      // sem /bin/sh: os argumentos vão como estão
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        // Sem HOME estável o adb não mantém a chave deste servidor, e a TV recusa a autenticação
        // a cada execução. O usuário do serviço é criado sem home no guia de instalação, então
        // esta é a diferença entre "funciona na bancada" e "funciona como serviço".
        if (!string.IsNullOrWhiteSpace(_options.AdbKeyDirectory))
        {
            psi.Environment["HOME"] = _options.AdbKeyDirectory.Trim();
        }

        return psi;
    }

    private async Task<AdbResult> RawAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            using var process = new Process { StartInfo = Info(args) };
            process.Start();

            // Ler os dois fluxos em paralelo: um buffer cheio do lado do adb trava o processo.
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);

            await EsperarOuMatarAsync(process, timeout, ct);
            return new AdbResult(process.ExitCode, await stdout, await stderr);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            _logger.LogError("adb não encontrado em '{Caminho}'. Instale com: apt-get install android-tools-adb", _options.AdbPath);
            return new AdbResult(AdbNaoEncontrado, string.Empty,
                $"O programa 'adb' não foi encontrado em '{_options.AdbPath}'.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new AdbResult(-1, string.Empty, $"O aparelho não respondeu em {timeout.TotalSeconds:0}s.");
        }
    }

    private async Task<byte[]?> RawBinaryAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            using var process = new Process { StartInfo = Info(args) };
            process.Start();

            using var buffer = new MemoryStream(512 * 1024);
            var copia = process.StandardOutput.BaseStream.CopyToAsync(buffer, ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);

            await EsperarOuMatarAsync(process, timeout, ct);
            await copia;

            var erro = await stderr;
            if (!string.IsNullOrWhiteSpace(erro))
            {
                _logger.LogDebug("screencap devolveu no stderr: {Erro}", erro.Trim());
            }
            return buffer.ToArray();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            _logger.LogError("adb não encontrado em '{Caminho}'.", _options.AdbPath);
            return null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private static async Task EsperarOuMatarAsync(Process process, TimeSpan timeout, CancellationToken ct)
    {
        using var limite = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limite.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(limite.Token);
        }
        catch (OperationCanceledException)
        {
            // Um adb pendurado seguraria o transporte do aparelho para sempre.
            try { process.Kill(entireProcessTree: true); } catch { /* já morreu */ }
            throw;
        }
    }

    /// <summary>Descreve um estado para o operador — é o texto que aparece no painel.</summary>
    public static string Describe(TvReachState state) => state switch
    {
        TvReachState.Online => "TV respondendo.",
        TvReachState.Offline => "A TV não respondeu. Verifique se o aparelho está ligado e na rede.",
        TvReachState.Unauthorized => "A TV recusou a chave DESTE servidor. Aceite o aviso de depuração na tela da TV — a autorização é por servidor e por usuário do sistema, então autorizar de outro computador não vale aqui.",
        TvReachState.AdbMissing => "O programa 'adb' não está instalado no servidor.",
        _ => "Estado desconhecido.",
    };
}
