using System.Text.RegularExpressions;

namespace HospitalAccess.Api.Services;

/// <summary>
/// Resolve o nome do serviço do Home Assistant a chamar num quarto.
///
/// Dois modelos convivem, e o que decide é o que está configurado:
/// - SEM marcador: um único script para todos os quartos, que recebe o quarto no payload
///   (<c>script.boas_vindas_leito</c> — o jeito original, e o padrão de quem nunca mexeu);
/// - COM marcador: um script POR quarto (<c>script.BV_{quarto}</c> → <c>script.BV_14</c>),
///   para quem prefere a automação inteira dentro do HA, sem template no script.
///
/// A CAIXA é preservada letra por letra. O <c>entity_id</c> do HA costuma ser minúsculo, mas
/// quem configura é quem sabe como os scripts da casa foram criados — normalizar aqui
/// transformaria um "não achei o serviço" claro num mistério.
/// </summary>
public static class HomeAssistantServiceName
{
    /// <summary>Marcador do número/id do quarto. O alias em inglês existe porque o payload chama o campo de "room".</summary>
    public const string RoomPlaceholder = "{quarto}";
    public const string RoomPlaceholderAlias = "{room}";

    /// <summary>
    /// O que o HA aceita dentro de um object_id: letra, dígito, sublinhado e hífen. Um quarto
    /// escrito "Quarto 101" viraria "script.BV_Quarto 101" — nome que o HA recusa, e numa
    /// chamada best-effort (boas-vindas) a recusa passaria despercebida.
    /// </summary>
    private static readonly Regex RoomShape = new("^[A-Za-z0-9_-]+$", RegexOptions.Compiled);

    /// <summary>Este modelo depende do quarto (tem marcador)?</summary>
    public static bool IsPerRoom(string? template) =>
        !string.IsNullOrWhiteSpace(template)
        && (template.Contains(RoomPlaceholder, StringComparison.OrdinalIgnoreCase)
            || template.Contains(RoomPlaceholderAlias, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Devolve o serviço a chamar, ou null quando não dá para chamar nada: modelo vazio, ou
    /// modelo por quarto num controlador sem o quarto preenchido. Null é uma RESPOSTA — quem
    /// chama transforma em mensagem, em vez de mandar "script.BV_" para o HA e colher um 400.
    /// </summary>
    public static string? Resolve(string? template, string? room)
    {
        if (string.IsNullOrWhiteSpace(template)) return null;
        template = template.Trim();

        if (!IsPerRoom(template)) return template;
        if (string.IsNullOrWhiteSpace(room)) return null;

        room = room.Trim();
        if (!RoomShape.IsMatch(room)) return null;

        var resolved = Replace(Replace(template, RoomPlaceholder, room), RoomPlaceholderAlias, room);
        return HomeAssistantPayload.ParseService(resolved) is null ? null : resolved;
    }

    /// <summary>Substituição sem diferenciar maiúsculas NO MARCADOR — o valor entra intocado.</summary>
    private static string Replace(string text, string placeholder, string value) =>
        text.Replace(placeholder, value, StringComparison.OrdinalIgnoreCase);
}
