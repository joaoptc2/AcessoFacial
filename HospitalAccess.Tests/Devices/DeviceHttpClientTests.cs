using System.Text;
using System.Text.Json;
using HospitalAccess.Infrastructure.Devices;
using Xunit;

namespace HospitalAccess.Tests.Devices;

/// <summary>
/// Trava o contrato byte-a-byte do cliente HTTP do FC-8190H (não testável contra hardware aqui).
/// Os vetores/pegadinhas vêm da engenharia reversa do painel: MD5(Hash+senha+Hash) MAIÚSCULO;
/// a parte PeopleJson do multipart SEM Content-Type; a foto como image/jpg; leitura do QRCode.
/// </summary>
public class DeviceHttpClientTests
{
    [Fact]
    public void LoginPasswordHash_matches_reference_algorithm()
    {
        // Vetor calculado independentemente em Python:
        //   hashlib.md5((h+p+h).encode()).hexdigest().upper()
        const string hash = "11111111-2222-3333-4444-555555555555";
        const string password = "FFFFFFFF";
        var result = DeviceHttpClient.ComputeLoginPasswordHash(hash, password);
        Assert.Equal("CE14214FCF52843ED2513DDBA7A2B4F9", result);
    }

    [Fact]
    public void LoginPasswordHash_is_uppercase_hex_32_chars()
    {
        var result = DeviceHttpClient.ComputeLoginPasswordHash(Guid.NewGuid().ToString(), "senha");
        Assert.Equal(32, result.Length);
        Assert.Matches("^[0-9A-F]{32}$", result);
    }

    [Fact]
    public void ExtractQrCode_finds_field_across_casings()
    {
        Assert.Equal("abc", ExtractFrom("""{"QRCode":"abc"}"""));
        Assert.Equal("abc", ExtractFrom("""{"qrcode":"abc"}"""));
        Assert.Equal("abc", ExtractFrom("""{"QrCode":"abc"}"""));
        Assert.Null(ExtractFrom("""{"QRCode":""}"""));
        Assert.Null(ExtractFrom("""{"Other":"x"}"""));

        static string? ExtractFrom(string json)
        {
            using var doc = JsonDocument.Parse(json);
            return DeviceHttpClient.ExtractQrCode(doc.RootElement);
        }
    }

    [Fact]
    public void Multipart_PeopleJson_part_has_no_content_type_and_photo_is_image_jpg()
    {
        var (body, contentType) = DeviceHttpClient.BuildMultipartBody(
            """{"UserID":"5"}""", photoBytes: new byte[] { 1, 2, 3 }, photoFilename: "1.jpg");
        var text = Encoding.UTF8.GetString(body);

        Assert.StartsWith("multipart/form-data; boundary=----facial", contentType);
        // A foto usa image/jpg (NÃO image/jpeg) — pegadinha do firmware.
        Assert.Contains("Content-Type: image/jpg\r\n", text);
        Assert.DoesNotContain("image/jpeg", text);
        // A parte PeopleJson NÃO pode ter Content-Type (senão o firmware fecha a conexão).
        var peopleIdx = text.IndexOf("name=\"PeopleJson\"", StringComparison.Ordinal);
        Assert.True(peopleIdx > 0);
        var afterPeople = text[peopleIdx..];
        var jsonIdx = afterPeople.IndexOf("""{"UserID":"5"}""", StringComparison.Ordinal);
        Assert.True(jsonIdx > 0);
        // Entre o cabeçalho da parte e o corpo JSON não deve haver "Content-Type".
        Assert.DoesNotContain("Content-Type", afterPeople[..jsonIdx]);
        // CRLF em todo lugar.
        Assert.Contains("\r\n", text);
    }

    [Fact]
    public void Multipart_without_photo_omits_photo_part()
    {
        var (body, _) = DeviceHttpClient.BuildMultipartBody("""{"UserID":"5"}""", photoBytes: null, photoFilename: "1.jpg");
        var text = Encoding.UTF8.GetString(body);
        Assert.DoesNotContain("name=\"Photo\"", text);
        Assert.Contains("name=\"PeopleJson\"", text);
    }

    // O firmware sinaliza token morto com HTTP 200 + errCode=10000 (nunca 401) — o cliente
    // precisa reconhecer esse envelope como falha de AUTENTICAÇÃO para re-logar e repetir.
    [Theory]
    [InlineData(10000, "Token is invalid", true)]
    [InlineData(10000, "qualquer texto", true)] // o código sozinho basta
    [InlineData(null, "Token is invalid", true)] // a mensagem sozinha basta
    [InlineData(null, "TOKEN IS INVALID", true)] // case-insensitive
    [InlineData(2, "Data error", false)]
    [InlineData(null, "erro qualquer", false)]
    public void Token_error_detection(int? errCode, string message, bool expected)
    {
        var ex = new DeviceHttpException($"[Quarto 14] POST /api/People/GetDetail errCode={errCode} error={message}",
            status: 200, errCode: errCode);
        Assert.Equal(expected, DeviceHttpClient.IsTokenError(ex));
    }
}
