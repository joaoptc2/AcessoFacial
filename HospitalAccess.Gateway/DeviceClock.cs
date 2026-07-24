namespace HospitalAccess.Gateway;

/// <summary>
/// Conversões entre UTC e o "relógio de parede" dos aparelhos (Device:TimeZone). O 8190H grava e
/// devolve TUDO em horário local do próprio relógio: a validade (Expiry) vai convertida para lá
/// e os registros lidos (push/coleta/fotos) voltam convertidos para UTC por aqui — nunca pelo
/// fuso do SO do servidor (produção roda em UTC e os eventos ficavam 3h atrasados).
/// </summary>
public static class DeviceClock
{
    /// <summary>UTC → horário local do aparelho (para escrever no dispositivo).</summary>
    public static DateTime ToWallClock(DateTime utc, TimeZoneInfo deviceTimeZone) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), deviceTimeZone);

    /// <summary>Horário local do aparelho → UTC (para gravar registros lidos do dispositivo).</summary>
    public static DateTime FromWallClock(DateTime deviceLocal, TimeZoneInfo deviceTimeZone) =>
        TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(deviceLocal, DateTimeKind.Unspecified), deviceTimeZone);
}
