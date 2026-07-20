using HospitalAccess.Api.Services;
using HospitalAccess.Domain.Enums;
using Xunit;

namespace HospitalAccess.Tests.Access;

public class SyncOverviewRulesTests
{
    private static readonly DateTime Now = new(2026, 07, 20, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Pending_e_pending_mesmo_com_next_retry_preenchido()
    {
        // O campo NextRetryAtUtc é ignorado fora de Failed (semântica da entidade).
        Assert.Equal(SyncPendingCategory.Pending,
            SyncOverviewRules.Categorize(SyncState.Pending, Now.AddMinutes(10), null, Now));
        Assert.Equal(SyncPendingCategory.Pending,
            SyncOverviewRules.Categorize(SyncState.Pending, null, null, Now));
    }

    [Fact]
    public void Conflito_tem_precedencia_sobre_quarentena()
    {
        // Conflito de duplicidade também fica com NextRetryAtUtc null (falha permanente),
        // mas tem fluxo próprio (substituir/manter) — nunca deve aparecer como quarentena.
        Assert.Equal(SyncPendingCategory.Conflict,
            SyncOverviewRules.Categorize(SyncState.Failed, null, 42u, Now));
    }

    [Fact]
    public void Failed_sem_next_retry_e_quarentena()
    {
        Assert.Equal(SyncPendingCategory.Quarantined,
            SyncOverviewRules.Categorize(SyncState.Failed, null, null, Now));
    }

    [Fact]
    public void Failed_com_backoff_vencido_e_elegivel_agora_fronteira_inclusiva()
    {
        Assert.Equal(SyncPendingCategory.DueNow,
            SyncOverviewRules.Categorize(SyncState.Failed, Now.AddMinutes(-1), null, Now));
        // Fronteira: exatamente == now conta como vencido (mesmo <= da query de elegibilidade).
        Assert.Equal(SyncPendingCategory.DueNow,
            SyncOverviewRules.Categorize(SyncState.Failed, Now, null, Now));
    }

    [Fact]
    public void Failed_com_backoff_futuro_esta_aguardando()
    {
        Assert.Equal(SyncPendingCategory.WaitingBackoff,
            SyncOverviewRules.Categorize(SyncState.Failed, Now.AddMinutes(5), null, Now));
    }

    [Theory]
    [InlineData(true, true, SyncPendingAction.Remove)]   // revogado (permissão fica p/ reativar)
    [InlineData(false, false, SyncPendingAction.Remove)] // permissão retirada → reconciliação remove
    [InlineData(true, false, SyncPendingAction.Remove)]
    [InlineData(false, true, SyncPendingAction.Send)]
    public void ResolveAction_remove_quando_revogado_ou_sem_permissao(bool revoked, bool hasPermission, SyncPendingAction expected)
    {
        Assert.Equal(expected, SyncOverviewRules.ResolveAction(revoked, hasPermission));
    }
}
