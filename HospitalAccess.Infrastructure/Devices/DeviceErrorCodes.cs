namespace HospitalAccess.Infrastructure.Devices;

/// <summary>
/// Tabelas de códigos do protocolo AI Series (FC-8190H), portadas do sistema de referência
/// (seções 3.1/3.2 e resultado de acesso). Traduzem os inteiros crus do firmware em algo legível
/// para logs e UI.
/// </summary>
public static class DeviceErrorCodes
{
    /// <summary>Códigos de erro básicos (seção 3.1). errCode do envelope {result,content,errCode,error}.</summary>
    public static readonly IReadOnlyDictionary<int, string> Basic = new Dictionary<int, string>
    {
        [0] = "OK", [-1] = "FAIL", [1] = "USER_EXISTS", [2] = "DATA_ERROR", [3] = "NO_FACE",
        [4] = "FEATURE_FAIL", [5] = "MULTIPLE_FACE", [6] = "SIZE_ERROR", [7] = "DECODE_FAIL",
        [8] = "COPY_ERROR", [9] = "NO_PIC", [10] = "DB_ERROR", [11] = "QUALITY_LOW",
        [12] = "SIMILARITY_HIGH", [13] = "REG_LIMIT", [14] = "FORMAT_ERROR", [15] = "INTERNAL_ERROR",
        [16] = "REG_DUPLICATE", [17] = "MIS_RECOGNIZED", [18] = "LATEST_VERSION", [19] = "INVALID_FIRMWARE",
        [20] = "NO_SPACE", [21] = "NOT_LOGIN", [22] = "NO_MEMORY", [23] = "PWD_ERROR",
        [24] = "SSID_LONG", [25] = "PSK_LONG", [26] = "DOOR_TOO_OFTEN",
        // Observado em produção (fora da tabela 3.1): token de login expirado/invalidado —
        // acontece após reinício do aparelho; o cliente re-loga sozinho ao detectar.
        [10000] = "TOKEN_INVALID",
    };

    /// <summary>
    /// Resultado de acesso (campo <c>notePass</c> dos eventos phone-home). ATENÇÃO: não é contíguo —
    /// não existe código 3, e 1 = liberado (0 = negado).
    /// </summary>
    public static readonly IReadOnlyDictionary<int, string> AccessResult = new Dictionary<int, string>
    {
        [0] = "FAILED", [1] = "PASSED", [2] = "NO_PERMISSION", [4] = "EXPIRED", [5] = "ACCESS_LIMIT_REACHED",
    };

    public static string DescribeError(int code) =>
        Basic.TryGetValue(code, out var v) ? v : $"UNKNOWN_CODE_{code}";

    public static string DescribeAccess(int code) =>
        AccessResult.TryGetValue(code, out var v) ? v : $"UNKNOWN_{code}";
}
