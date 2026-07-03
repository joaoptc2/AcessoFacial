using HospitalAccess.Domain.Enums;

namespace HospitalAccess.Domain.Protocol;

/// <summary>
/// Classificação dos códigos de evento do protocolo 8190H (registro de autenticação, §9.2, e
/// log de sistema, §9.4). Lógica pura, sem dependência do SDK — daí ser testável isoladamente.
/// </summary>
public static class TransactionCodeClassifier
{
    /// <summary>
    /// Códigos de TransactionCode do registro de autenticação (§9.2) que representam acesso
    /// CONCEDIDO. Faixa 1–14 = combinações de verificação válidas (inclui 13 = Face+Digital+Senha;
    /// o código que significa "não abre" é o 22, não o 13). 15 = verificação repetida. 16–30 =
    /// razões de negação, exceto 25 (abertura sem verificação) e 29 (validação OK, validade
    /// prestes a expirar), que são concedidos.
    /// </summary>
    private static readonly HashSet<int> GrantedCodes =
        new() { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 25, 29 };

    /// <summary>Mapa dos códigos base do log de sistema (§9.4) para AlarmKind.</summary>
    private static readonly Dictionary<int, AlarmKind> SystemAlarmBaseCodes = new()
    {
        [14] = AlarmKind.InvalidCard,     // verificação ilegal
        [15] = AlarmKind.DoorSensor,      // sensor de porta (arrombamento)
        [16] = AlarmKind.Duress,          // coação
        [17] = AlarmKind.OpenDoorTimeout, // porta aberta além do tempo
        [18] = AlarmKind.Blacklist,       // lista negra
        [19] = AlarmKind.Fire,            // incêndio
        [20] = AlarmKind.AntiTheft,       // sabotagem/tamper
    };

    /// <summary>True se o código de autenticação representa acesso concedido.</summary>
    public static bool IsAccessGranted(int transactionCode) => GrantedCodes.Contains(transactionCode);

    /// <summary>
    /// Tenta mapear um código do log de sistema para um alarme. Códigos 14–20 são disparos;
    /// 21–27 são o cancelamento (remoção) dos respectivos alarmes (base = código − 7). Retorna
    /// false para logs de sistema que não são alarmes (unlock por software, energia etc.).
    /// </summary>
    public static bool TryMapSystemAlarm(int systemCode, out AlarmKind kind, out bool cleared)
    {
        cleared = systemCode is >= 21 and <= 27;
        var baseCode = cleared ? systemCode - 7 : systemCode;
        return SystemAlarmBaseCodes.TryGetValue(baseCode, out kind);
    }
}
