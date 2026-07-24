namespace HospitalAccess.Gateway;

/// <summary>
/// Fonte da senha de comunicação PADRÃO dos aparelhos (a global das Configurações). Um
/// controlador com <c>CommunicationPassword</c> vazia usa este padrão — implementado na camada
/// de API (RuntimeSettingsProvider), injetado aqui para o factory de conexões não depender dela.
/// </summary>
public interface IDeviceSecretDefaults
{
    /// <summary>Senha de comunicação padrão; null/vazia = nenhum padrão configurado.</summary>
    string? DefaultCommunicationPassword { get; }
}
