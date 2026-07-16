using HospitalAccess.Api.Services;
using Xunit;

namespace HospitalAccess.Tests.Devices;

/// <summary>
/// Trava a calibragem do aviso de "gargalo local" do health-check: exige escala ABSOLUTA
/// (≥3 falhas simultâneas) além da proporção (≥ metade da frota). A regra antiga
/// (offline ≥ total/2) disparava alarme falso com UM aparelho fora numa frota de 3 —
/// exatamente o log recebido em produção.
/// </summary>
public class HealthProbeHeuristicsTests
{
    [Theory]
    // Frota pequena: 1 ou 2 fora é rotina de manutenção, não gargalo.
    [InlineData(0, 3, false)]
    [InlineData(1, 3, false)] // o caso do alarme falso em produção
    [InlineData(2, 3, false)]
    [InlineData(3, 3, true)]  // a frota INTEIRA de uma vez, aí sim
    [InlineData(1, 1, false)] // frota de 1: nunca é "muitos de uma vez"
    [InlineData(2, 2, false)]
    // Frota alvo (~30): metade ou mais, com mínimo absoluto de 3.
    [InlineData(3, 6, true)]
    [InlineData(2, 4, false)]  // proporção atingida mas escala absoluta não
    [InlineData(14, 30, false)]
    [InlineData(15, 30, true)]
    [InlineData(30, 30, true)]
    public void SuspectLocalBottleneck_Boundaries(int offline, int total, bool expected) =>
        Assert.Equal(expected, HealthProbeHeuristics.SuspectLocalBottleneck(offline, total));
}
