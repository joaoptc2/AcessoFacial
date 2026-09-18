using HospitalAccess.Api.Services;
using HospitalAccess.Domain.Entities;
using HospitalAccess.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HospitalAccess.Api.Controllers;

/// <summary>
/// Manutenção remota da TV do quarto. O caso de uso é um só: quando a conta de um serviço de
/// streaming cai num leito, o operador vê a tela e loga de novo da mesa, sem entrar no quarto.
///
/// <para>
/// RBAC: a Recepção NÃO entra aqui. A tela da TV mostra a imagem de boas-vindas com o nome do
/// paciente — ver o quarto é ação de manutenção, não de atendimento. Admin e Operador operam;
/// as ações que destroem estado (reiniciar, limpar dados, bloquear app) são só de Admin.
/// </para>
/// <para>
/// Auditoria: todo comando de escrita vira uma linha em <see cref="ControllerAuditLog"/>. O
/// <c>GET status</c> também, porque é a chamada que o painel faz ao ABRIR — é ela que registra
/// quem olhou a tela de qual quarto e quando. A captura de tela em si não é auditada: ela
/// recarrega a cada segundo e inundaria a tabela; o registro de abertura é o que responde a
/// pergunta de auditoria. O conteúdo digitado NUNCA é registrado — costuma ser senha.
/// </para>
/// </summary>
[ApiController]
[Route("api/tv")]
[Authorize(Roles = "Admin,Operator")]
public sealed class TvController : ControllerBase
{
    private readonly AccessDbContext _db;
    private readonly AdbClient _adb;

    public TvController(AccessDbContext db, AdbClient adb)
    {
        _db = db;
        _adb = adb;
    }

    // ------------------------------------------------------------------ leitura

    /// <summary>
    /// Estado do stick: alcançável, há quanto tempo ligado, o que está na tela. É a chamada de
    /// abertura do painel — e a que entra na auditoria.
    /// </summary>
    [HttpGet("{id:guid}/status")]
    public async Task<IActionResult> Status(Guid id, CancellationToken ct)
    {
        var alvo = await Localizar(id, ct);
        if (alvo.Error is not null) return alvo.Error;
        var (controller, address) = (alvo.Controller!, alvo.Address);

        var alcance = await _adb.ConnectAsync(address, ct);
        if (alcance.State is not TvReachState.Online)
        {
            var motivo = AdbClient.Describe(alcance.State);
            await Auditar(controller, "AbrirPainelTV", success: false, alcance.Detail is { Length: > 0 } d ? d : motivo, ct);
            return Ok(new TvStatusDto(false, alcance.State.ToString(), motivo, null, null, null, alcance.Detail));
        }

        // Um único ida-e-volta com marcadores: modelo, uptime e foco. Comando constante — nada
        // vindo do operador entra nesta linha.
        var result = await _adb.ShellAsync(address,
        [
            "echo", "__MODELO__", ";", "getprop", "ro.product.model", ";",
            "echo", "__UPTIME__", ";", "cat", "/proc/uptime", ";",
            "echo", "__FOCO__", ";", "dumpsys", "window", "|", "grep", "-m1", "mCurrentFocus",
        ], _adb.CommandTimeout, ct);

        var estado = AdbClient.Classify(result);
        if (estado is not TvReachState.Online)
        {
            var detalhe = AdbClient.Detail(result);
            var motivo = AdbClient.Describe(estado);
            await Auditar(controller, "AbrirPainelTV", success: false, detalhe is { Length: > 0 } ? detalhe : motivo, ct);
            return Ok(new TvStatusDto(false, estado.ToString(), motivo, null, null, null, detalhe));
        }

        var secoes = AdbCommandRules.ParseMarkedSections(result.StdOut, "__MODELO__", "__UPTIME__", "__FOCO__");
        var foco = AdbCommandRules.ParseCurrentFocus(secoes.GetValueOrDefault("__FOCO__"));

        controller.TvLastSeenUtc = DateTime.UtcNow;
        await Auditar(controller, "AbrirPainelTV", success: true, null, ct);

        return Ok(new TvStatusDto(
            Online: true,
            State: nameof(TvReachState.Online),
            Message: AdbClient.Describe(TvReachState.Online),
            Model: secoes.GetValueOrDefault("__MODELO__"),
            UptimeSeconds: AdbCommandRules.ParseUptimeSeconds(secoes.GetValueOrDefault("__UPTIME__")),
            Focus: foco));
    }

    /// <summary>
    /// PNG da tela do quarto, agora. <c>no-store</c> porque a imagem contém o nome do paciente —
    /// não pode ficar no cache do navegador nem de proxy nenhum. 502 quando o aparelho não
    /// entregou (desligado, sem rede, ou tela protegida por DRM devolvendo lixo).
    /// </summary>
    [HttpGet("{id:guid}/screen")]
    public async Task<IActionResult> Screen(Guid id, CancellationToken ct)
    {
        var alvo = await Localizar(id, ct);
        if (alvo.Error is not null) return alvo.Error;
        var (controller, address) = (alvo.Controller!, alvo.Address);

        var png = await _adb.ScreenshotAsync(address, ct);
        if (png is null) return StatusCode(502, new { error = "Não foi possível capturar a tela da TV." });

        Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        Response.Headers.Pragma = "no-cache";
        return File(png, "image/png");
    }

    /// <summary>Apps instalados pelo usuário neste quarto (não lista os do sistema).</summary>
    [HttpGet("{id:guid}/apps")]
    public async Task<IActionResult> Apps(Guid id, CancellationToken ct)
    {
        var alvo = await Localizar(id, ct);
        if (alvo.Error is not null) return alvo.Error;
        var (controller, address) = (alvo.Controller!, alvo.Address);

        var result = await _adb.ShellAsync(address, ["pm", "list", "packages", "-3"], _adb.CommandTimeout, ct);
        if (!result.Ok) return StatusCode(502, new { error = AdbClient.Describe(AdbClient.Classify(result)) });

        return Ok(AdbCommandRules.ParsePackageList(result.StdOut));
    }

    // ------------------------------------------------------------------ operar a tela

    /// <summary>Uma tecla do controle remoto. Só as da lista fechada em <see cref="AdbCommandRules.AllowedKeys"/>.</summary>
    [HttpPost("{id:guid}/key")]
    public async Task<IActionResult> Key(Guid id, [FromBody] TvKeyRequest body, CancellationToken ct)
    {
        if (!AdbCommandRules.IsAllowedKey(body?.Key))
            return BadRequest(new { error = $"Tecla não permitida: '{body?.Key}'." });

        var alvo = await Localizar(id, ct);
        if (alvo.Error is not null) return alvo.Error;
        var (controller, address) = (alvo.Controller!, alvo.Address);

        var result = await _adb.ShellAsync(address, ["input", "keyevent", body!.Key], _adb.CommandTimeout, ct);
        return await Concluir(controller, $"TeclaTV:{body.Key}", result, ct);
    }

    /// <summary>
    /// Digita uma linha no campo que estiver em foco — é como a senha entra sem datilografar no
    /// direcional. O texto é dividido conforme <see cref="AdbCommandRules.TryEncodeInputText"/> e
    /// enviado em ordem; o CONTEÚDO não vai para a auditoria.
    /// </summary>
    [HttpPost("{id:guid}/text")]
    public async Task<IActionResult> Text(Guid id, [FromBody] TvTextRequest body, CancellationToken ct)
    {
        if (!AdbCommandRules.TryEncodeInputText(body?.Text, out var pedacos, out var motivo))
            return BadRequest(new { error = motivo });

        var alvo = await Localizar(id, ct);
        if (alvo.Error is not null) return alvo.Error;
        var (controller, address) = (alvo.Controller!, alvo.Address);

        AdbResult result = new(0, string.Empty, string.Empty);
        foreach (var pedaco in pedacos)
        {
            result = await _adb.ShellAsync(address,
                ["input", "text", AdbCommandRules.QuoteForDeviceShell(pedaco)], _adb.CommandTimeout, ct);
            if (!result.Ok) break;
        }

        // Auditoria sem o texto: "digitou algo", nunca o quê.
        return await Concluir(controller, "DigitarTextoTV", result, ct);
    }

    /// <summary>Abre um app no quarto. Em Android TV a categoria é LEANBACK_LAUNCHER, não LAUNCHER.</summary>
    [HttpPost("{id:guid}/apps/{package}/launch")]
    public async Task<IActionResult> Launch(Guid id, string package, CancellationToken ct)
    {
        if (!AdbCommandRules.IsValidPackageName(package))
            return BadRequest(new { error = $"Nome de aplicativo inválido: '{package}'." });

        var alvo = await Localizar(id, ct);
        if (alvo.Error is not null) return alvo.Error;
        var (controller, address) = (alvo.Controller!, alvo.Address);

        var result = await _adb.ShellAsync(address,
            ["monkey", "-p", package, "-c", "android.intent.category.LEANBACK_LAUNCHER", "1"],
            _adb.CommandTimeout, ct);

        if (result.Ok && result.Output.Contains("No activities found", StringComparison.OrdinalIgnoreCase))
        {
            await Auditar(controller, $"AbrirAppTV:{package}", success: false, "sem atividade de entrada", ct);
            return StatusCode(502, new { error = $"'{package}' não tem tela de entrada neste aparelho." });
        }

        return await Concluir(controller, $"AbrirAppTV:{package}", result, ct);
    }

    /// <summary>Encerra um app travado no quarto.</summary>
    [HttpPost("{id:guid}/apps/{package}/stop")]
    public async Task<IActionResult> Stop(Guid id, string package, CancellationToken ct)
    {
        if (!AdbCommandRules.IsValidPackageName(package))
            return BadRequest(new { error = $"Nome de aplicativo inválido: '{package}'." });

        var alvo = await Localizar(id, ct);
        if (alvo.Error is not null) return alvo.Error;
        var (controller, address) = (alvo.Controller!, alvo.Address);

        var result = await _adb.ShellAsync(address, ["am", "force-stop", package], _adb.CommandTimeout, ct);
        return await Concluir(controller, $"FecharAppTV:{package}", result, ct);
    }

    // ------------------------------------------------------------------ manutenção (Admin)

    /// <summary>Reinicia o stick. O aparelho some da rede por ~40s — o painel avisa e espera.</summary>
    [HttpPost("{id:guid}/reboot")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Reboot(Guid id, CancellationToken ct)
    {
        var alvo = await Localizar(id, ct);
        if (alvo.Error is not null) return alvo.Error;
        var (controller, address) = (alvo.Controller!, alvo.Address);

        var result = await _adb.DeviceAsync(address, ["reboot"], _adb.CommandTimeout, ct);
        return await Concluir(controller, "ReiniciarTV", result, ct);
    }

    /// <summary>
    /// Zera os dados de um app. DERRUBA O LOGIN dele — é o último recurso para um app que não
    /// abre, não uma rotina. Depois disso alguém precisa logar de novo, pelo próprio painel.
    /// </summary>
    [HttpPost("{id:guid}/apps/{package}/clear")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Clear(Guid id, string package, CancellationToken ct)
    {
        if (!AdbCommandRules.IsValidPackageName(package))
            return BadRequest(new { error = $"Nome de aplicativo inválido: '{package}'." });

        var alvo = await Localizar(id, ct);
        if (alvo.Error is not null) return alvo.Error;
        var (controller, address) = (alvo.Controller!, alvo.Address);

        var result = await _adb.ShellAsync(address, ["pm", "clear", package], _adb.LongCommandTimeout, ct);
        return await Concluir(controller, $"LimparDadosAppTV:{package}", result, ct);
    }

    /// <summary>
    /// Bloqueia ou libera um app. Um app bloqueado continua visível e avisa que está indisponível
    /// ao ser aberto — melhor que sumir da tela e confundir quem está no quarto. Funciona
    /// inclusive com as Configurações do aparelho.
    /// </summary>
    [HttpPost("{id:guid}/apps/{package}/suspend")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Suspend(Guid id, string package, [FromBody] TvSuspendRequest body, CancellationToken ct)
    {
        if (!AdbCommandRules.IsValidPackageName(package))
            return BadRequest(new { error = $"Nome de aplicativo inválido: '{package}'." });

        var alvo = await Localizar(id, ct);
        if (alvo.Error is not null) return alvo.Error;
        var (controller, address) = (alvo.Controller!, alvo.Address);

        var bloquear = body?.Suspended ?? true;
        var result = await _adb.ShellAsync(address,
            ["pm", bloquear ? "suspend" : "unsuspend", "--user", "0", package],
            _adb.CommandTimeout, ct);

        return await Concluir(controller, $"{(bloquear ? "BloquearAppTV" : "LiberarAppTV")}:{package}", result, ct);
    }

    // ------------------------------------------------------------------ apoio

    /// <summary>
    /// Resolve o leito e o endereço do stick. Devolve o erro pronto quando o leito não existe ou
    /// não tem TV cadastrada — 404, e não 400, porque do ponto de vista da rota o recurso
    /// "TV deste leito" não existe.
    /// </summary>
    private async Task<(Domain.Entities.Controller? Controller, string Address, IActionResult? Error)> Localizar(
        Guid id, CancellationToken ct)
    {
        var controller = await _db.Controllers.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (controller is null) return (null, string.Empty, NotFound());

        if (!AdbCommandRules.TryBuildDeviceAddress(controller.TvIpAddress, controller.TvPort, out var address, out var motivo))
            return (null, string.Empty, NotFound(new { error = motivo }));

        return (controller, address, null);
    }

    /// <summary>Audita e traduz a saída do adb em 204 ou 502.</summary>
    private async Task<IActionResult> Concluir(
        Domain.Entities.Controller controller, string acao, AdbResult result, CancellationToken ct)
    {
        var estado = AdbClient.Classify(result);
        if (estado is TvReachState.Online)
        {
            controller.TvLastSeenUtc = DateTime.UtcNow;
            await Auditar(controller, acao, success: true, null, ct);
            return NoContent();
        }

        var mensagem = AdbClient.Describe(estado);
        await Auditar(controller, acao, success: false, result.Output is { Length: > 0 } o ? o : mensagem, ct);
        return StatusCode(502, new { error = mensagem, detail = result.Output });
    }

    private async Task Auditar(
        Domain.Entities.Controller controller, string acao, bool success, string? error, CancellationToken ct)
    {
        _db.ControllerAuditLogs.Add(new ControllerAuditLog
        {
            ControllerId = controller.Id,
            ControllerName = controller.Name,
            Action = acao,
            PerformedByUsername = User.Identity?.Name,
            Success = success,
            Error = error,
        });
        await _db.SaveChangesAsync(ct);
    }
}

/// <summary>
/// Estado do stick para o painel. Model/Uptime/Focus só vêm quando <paramref name="Online"/>.
/// <paramref name="Detail"/> é a saída literal do adb, mostrada quando algo falha: sem ela,
/// "a TV não respondeu" some com a única informação capaz de apontar a correção.
/// </summary>
public sealed record TvStatusDto(
    bool Online,
    string State,
    string Message,
    string? Model,
    double? UptimeSeconds,
    string? Focus,
    string? Detail = null);

public sealed record TvKeyRequest(string Key);

public sealed record TvTextRequest(string Text);

public sealed record TvSuspendRequest(bool Suspended);
