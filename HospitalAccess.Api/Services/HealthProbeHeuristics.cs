namespace HospitalAccess.Api.Services;

/// <summary>
/// Heurísticas puras do health-check (extraídas para teste).
/// </summary>
public static class HealthProbeHeuristics
{
    /// <summary>
    /// "Muitos aparelhos caíram DE UMA VEZ" sugere gargalo LOCAL (servidor saturado), não falha
    /// dos aparelhos — o sintoma histórico do health-check via SDK. Exige escala ABSOLUTA
    /// (≥ 3 falhas simultâneas) além da proporção (≥ metade da frota): a regra antiga era só
    /// "offline ≥ total/2", que numa frota de 3 disparava o alarme de gargalo para UM único
    /// aparelho desligado — alarme falso (o caso normal de manutenção/cabo/energia).
    /// </summary>
    public static bool SuspectLocalBottleneck(int offline, int total) =>
        offline >= 3 && offline * 2 >= total;
}
