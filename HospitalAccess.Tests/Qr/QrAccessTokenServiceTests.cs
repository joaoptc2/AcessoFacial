using System.Text;
using HospitalAccess.Application.Qr;
using Xunit;

namespace HospitalAccess.Tests.Qr;

public class QrAccessTokenServiceTests
{
    private readonly QrAccessTokenService _svc = new();

    [Fact]
    public void BuildAccessToken_MatchesVendorReferenceFormat()
    {
        // QR real e funcional fornecido pelo cliente em 2026-07-02: decodifica (Base64 ->
        // ASCII) para "user_id=1_time=1782921297138761".
        var timestampUtc = DateTime.UnixEpoch.AddTicks(1782921297138761L * 10);
        var token = _svc.BuildAccessToken(1, timestampUtc);

        var decoded = Encoding.ASCII.GetString(Convert.FromBase64String(token));
        Assert.Equal("user_id=1_time=1782921297138761", decoded);
    }

    [Fact]
    public void BuildAccessToken_IsValidBase64()
    {
        var token = _svc.BuildAccessToken(42, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.NotNull(Convert.FromBase64String(token));
    }

    [Fact]
    public void BuildAccessToken_EncodesUserCodeAndMicrosecondTimestamp()
    {
        var timestampUtc = new DateTime(2026, 7, 2, 12, 0, 0, DateTimeKind.Utc);
        var expectedMicros = (long)(timestampUtc - DateTime.UnixEpoch).TotalMicroseconds;

        var token = _svc.BuildAccessToken(7, timestampUtc);
        var decoded = Encoding.ASCII.GetString(Convert.FromBase64String(token));

        Assert.Equal($"user_id=7_time={expectedMicros}", decoded);
    }
}
