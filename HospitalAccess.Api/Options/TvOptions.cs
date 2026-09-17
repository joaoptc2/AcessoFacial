namespace HospitalAccess.Api.Options;

/// <summary>
/// Canal ADB com os sticks de TV dos quartos. Os padrões servem a uma instalação Debian/Ubuntu
/// com <c>android-tools-adb</c> instalado pelo apt — normalmente não é preciso configurar nada.
/// </summary>
public sealed class TvOptions
{
    public const string SectionName = "Tv";

    /// <summary>
    /// Caminho do executável do adb. "adb" resolve pelo PATH. Aponte para o caminho completo se o
    /// serviço rodar com um PATH enxuto.
    /// </summary>
    public string AdbPath { get; set; } = "adb";

    /// <summary>Timeout de um comando comum (status, tecla, texto). Segundos.</summary>
    public int CommandTimeoutSeconds { get; set; } = 15;

    /// <summary>
    /// Timeout da captura de tela, maior porque o aparelho comprime o PNG antes de responder.
    /// Segundos.
    /// </summary>
    public int ScreenshotTimeoutSeconds { get; set; } = 25;

    /// <summary>
    /// Timeout de operações demoradas por natureza: instalar APK, limpar dados de app. Segundos.
    /// </summary>
    public int LongCommandTimeoutSeconds { get; set; } = 120;
}
