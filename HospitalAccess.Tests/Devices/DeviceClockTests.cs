using HospitalAccess.Gateway;
using Xunit;

namespace HospitalAccess.Tests.Devices;

public class DeviceClockTests
{
    // Fuso fixo -3h (mesmo fallback do Program.cs): determinístico em qualquer máquina de build.
    private static readonly TimeZoneInfo Tz =
        TimeZoneInfo.CreateCustomTimeZone("Test-3", TimeSpan.FromHours(-3), "Test-3", "Test-3");

    [Fact]
    public void FromWallClock_converte_horario_do_aparelho_para_utc()
    {
        // 08:31 no relógio do aparelho (UTC-3) = 11:31 UTC.
        var deviceLocal = new DateTime(2026, 07, 23, 8, 31, 5, DateTimeKind.Unspecified);

        var utc = DeviceClock.FromWallClock(deviceLocal, Tz);

        Assert.Equal(new DateTime(2026, 07, 23, 11, 31, 5), utc);
        Assert.Equal(DateTimeKind.Utc, utc.Kind);
    }

    [Fact]
    public void FromWallClock_ignora_kind_local_do_so_do_servidor()
    {
        // O SDK pode materializar com Kind=Local (fuso do SO). O valor É o relógio do aparelho —
        // a conversão deve usar SEMPRE o Device:TimeZone, nunca o fuso do servidor.
        var deviceLocal = new DateTime(2026, 07, 23, 8, 0, 0, DateTimeKind.Local);

        var utc = DeviceClock.FromWallClock(deviceLocal, Tz);

        Assert.Equal(new DateTime(2026, 07, 23, 11, 0, 0), utc);
    }

    [Fact]
    public void ToWallClock_converte_utc_para_horario_do_aparelho()
    {
        var utc = new DateTime(2026, 07, 23, 11, 31, 5, DateTimeKind.Utc);

        var wall = DeviceClock.ToWallClock(utc, Tz);

        Assert.Equal(new DateTime(2026, 07, 23, 8, 31, 5), wall);
    }

    [Fact]
    public void Roundtrip_preserva_o_instante()
    {
        var utc = new DateTime(2026, 07, 23, 23, 59, 59, DateTimeKind.Utc);

        var roundtrip = DeviceClock.FromWallClock(DeviceClock.ToWallClock(utc, Tz), Tz);

        Assert.Equal(utc, roundtrip);
    }
}
