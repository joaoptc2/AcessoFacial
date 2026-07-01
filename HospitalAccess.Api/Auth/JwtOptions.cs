namespace HospitalAccess.Api.Auth;

/// <summary>
/// Configuração do JWT. A chave (Key) DEVE vir de secrets/config protegida
/// (user-secrets em dev, variável de ambiente/Key Vault em produção) — nunca no código
/// nem em appsettings.json versionado.
/// </summary>
public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    public string Key { get; set; } = string.Empty;
    public string Issuer { get; set; } = "HospitalAccess";
    public string Audience { get; set; } = "HospitalAccess";
    public int ExpirationMinutes { get; set; } = 60;
}
