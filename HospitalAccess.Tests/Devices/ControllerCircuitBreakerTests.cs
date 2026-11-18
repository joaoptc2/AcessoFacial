using HospitalAccess.Gateway.Connections;
using Xunit;

namespace HospitalAccess.Tests.Devices;

/// <summary>
/// Trava o disjuntor por controlador: abre na 3ª falha consecutiva, cooldown exponencial com
/// teto, meia-abertura (1 falha reabre) e fechamento por sucesso.
/// </summary>
public class ControllerCircuitBreakerTests
{
    private static readonly DateTime T0 = new(2026, 7, 16, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Opens_OnlyAtThirdConsecutiveFailure()
    {
        var breaker = new ControllerCircuitBreaker();
        Assert.Null(breaker.RecordFailure(T0));
        Assert.Null(breaker.RecordFailure(T0));
        Assert.Null(breaker.OpenUntil(T0)); // 2 falhas: ainda fechado

        var openUntil = breaker.RecordFailure(T0);
        Assert.Equal(T0 + ControllerCircuitBreaker.BaseCooldown, openUntil);
        Assert.Equal(openUntil, breaker.OpenUntil(T0));
    }

    [Fact]
    public void Success_ResetsFailureCount()
    {
        var breaker = new ControllerCircuitBreaker();
        breaker.RecordFailure(T0);
        breaker.RecordFailure(T0);
        breaker.RecordSuccess(); // zera — as 2 falhas não contam mais

        Assert.Null(breaker.RecordFailure(T0));
        Assert.Null(breaker.OpenUntil(T0));
    }

    [Fact]
    public void OpenUntil_ExpiresAfterCooldown()
    {
        var breaker = new ControllerCircuitBreaker();
        for (var i = 0; i < ControllerCircuitBreaker.FailureThreshold; i++) breaker.RecordFailure(T0);

        Assert.NotNull(breaker.OpenUntil(T0 + TimeSpan.FromSeconds(59)));
        Assert.Null(breaker.OpenUntil(T0 + ControllerCircuitBreaker.BaseCooldown)); // meia-aberto: fluxo volta
    }

    [Fact]
    public void HalfOpen_SingleFailureReopensWithDoubledCooldown()
    {
        var breaker = new ControllerCircuitBreaker();
        for (var i = 0; i < ControllerCircuitBreaker.FailureThreshold; i++) breaker.RecordFailure(T0);

        var afterCooldown = T0 + ControllerCircuitBreaker.BaseCooldown + TimeSpan.FromSeconds(1);
        // Meia-aberto: UMA falha já reabre (não precisa de mais 3 marteladas no aparelho doente)
        var reopened = breaker.RecordFailure(afterCooldown);
        Assert.Equal(afterCooldown + TimeSpan.FromTicks(ControllerCircuitBreaker.BaseCooldown.Ticks * 2), reopened);
    }

    [Fact]
    public void Cooldown_IsCappedAtMax()
    {
        var breaker = new ControllerCircuitBreaker();
        var now = T0;
        for (var i = 0; i < ControllerCircuitBreaker.FailureThreshold; i++) breaker.RecordFailure(now);

        // Reaberturas sucessivas dobram o cooldown até o teto de 15 min.
        DateTime? until = breaker.OpenUntil(now);
        for (var round = 0; round < 10; round++)
        {
            now = until!.Value.AddSeconds(1);
            until = breaker.RecordFailure(now);
        }
        Assert.Equal(now + ControllerCircuitBreaker.MaxCooldown, until);
    }

    [Fact]
    public void Success_AfterReopen_FullyCloses()
    {
        var breaker = new ControllerCircuitBreaker();
        for (var i = 0; i < ControllerCircuitBreaker.FailureThreshold; i++) breaker.RecordFailure(T0);
        breaker.RecordSuccess(); // ex.: test-connection manual funcionou

        Assert.Null(breaker.OpenUntil(T0));
        // E o histórico zera: voltam a ser necessárias 3 falhas para abrir de novo.
        Assert.Null(breaker.RecordFailure(T0));
        Assert.Null(breaker.RecordFailure(T0));
        Assert.NotNull(breaker.RecordFailure(T0));
    }

    // ---- Prova de vida (IsHealthy): decide se vale mandar trabalho PESADO à porta.

    [Fact]
    public void Healthy_FromTheStart()
    {
        Assert.True(new ControllerCircuitBreaker().IsHealthy);
    }

    /// <summary>
    /// Uma falha já basta para perder a prova de vida, mesmo sem abrir o circuito: numa porta onde
    /// leituras leves passam e o upload de face falha, é isso que impede o próximo upload de ir
    /// antes de a porta responder de novo.
    /// </summary>
    [Fact]
    public void NotHealthy_AfterASingleFailure_EvenWithCircuitClosed()
    {
        var breaker = new ControllerCircuitBreaker();
        breaker.RecordFailure(T0);

        Assert.Null(breaker.OpenUntil(T0));
        Assert.False(breaker.IsHealthy);
    }

    /// <summary>
    /// O caso que originou a propriedade: vencido o cooldown, o circuito deixa de estar aberto —
    /// mas a porta não provou nada. Ao abrir, o disjuntor zera o contador de falhas; se a prova de
    /// vida olhasse só para ele, a meia-abertura passaria por porta saudável.
    /// </summary>
    [Fact]
    public void NotHealthy_InHalfOpen_AfterCooldownExpires()
    {
        var breaker = new ControllerCircuitBreaker();
        breaker.RecordFailure(T0);
        breaker.RecordFailure(T0);
        breaker.RecordFailure(T0); // abre

        var depoisDoCooldown = T0 + ControllerCircuitBreaker.BaseCooldown + TimeSpan.FromSeconds(1);
        Assert.Null(breaker.OpenUntil(depoisDoCooldown));
        Assert.False(breaker.IsHealthy);
    }

    [Fact]
    public void Healthy_AgainAfterSuccess()
    {
        var breaker = new ControllerCircuitBreaker();
        breaker.RecordFailure(T0);
        breaker.RecordFailure(T0);
        breaker.RecordFailure(T0);
        breaker.RecordSuccess();

        Assert.True(breaker.IsHealthy);
    }
}
