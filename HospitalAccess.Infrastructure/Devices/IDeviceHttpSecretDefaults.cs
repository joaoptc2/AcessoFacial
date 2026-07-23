namespace HospitalAccess.Infrastructure.Devices;

/// <summary>
/// Fonte em runtime da senha PADRÃO do painel web dos aparelhos (configurável pela tela de
/// Configurações). Tem precedência sobre o appsettings (Device:DefaultApiPassword) e é
/// sobreposta pela senha própria do controlador, quando preenchida.
/// </summary>
public interface IDeviceHttpSecretDefaults
{
    /// <summary>Senha padrão do painel web; null/vazia = usar o appsettings.</summary>
    string? DefaultApiPassword { get; }
}
