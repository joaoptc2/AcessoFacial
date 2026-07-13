using HospitalAccess.Api.Services;
using Xunit;

namespace HospitalAccess.Tests.Access;

/// <summary>
/// Testa a lógica pura de propagação de portas do grupo (modelo "base + extras manuais"): o
/// conjunto efetivo de portas de um usuário e o efeito de mudar as portas do grupo em um membro.
/// </summary>
public class GroupAccessServiceTests
{
    private static readonly Guid G = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid DoorA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid DoorB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Guid DoorC = Guid.Parse("cccccccc-0000-0000-0000-000000000003");

    [Fact]
    public void Effective_GroupDoorsInherited_ManualExtrasKeptNull()
    {
        // Manual: A. Grupo: B, C.
        var eff = GroupAccessService.ComputeEffectivePermissions(new[] { DoorA }, new[] { DoorB, DoorC }, G);

        Assert.Equal(3, eff.Count);
        Assert.Null(eff[DoorA]);   // manual
        Assert.Equal(G, eff[DoorB]); // herdada
        Assert.Equal(G, eff[DoorC]); // herdada
    }

    [Fact]
    public void Effective_DoorThatIsBothManualAndGroup_IsOwnedByGroup()
    {
        // A está tanto no "desejado manual" quanto nas portas do grupo → o grupo é dono (não null).
        var eff = GroupAccessService.ComputeEffectivePermissions(new[] { DoorA }, new[] { DoorA }, G);

        Assert.Single(eff);
        Assert.Equal(G, eff[DoorA]);
    }

    [Fact]
    public void Effective_NoGroup_AllManual()
    {
        var eff = GroupAccessService.ComputeEffectivePermissions(new[] { DoorA, DoorB }, Array.Empty<Guid>(), null);

        Assert.Equal(2, eff.Count);
        Assert.Null(eff[DoorA]);
        Assert.Null(eff[DoorB]);
    }

    [Fact]
    public void Effective_GroupDoorAlwaysPresentEvenIfNotDesired()
    {
        // Cliente mandou só A (manual), mas o grupo tem B: B continua presente (dono do grupo).
        var eff = GroupAccessService.ComputeEffectivePermissions(new[] { DoorA }, new[] { DoorB }, G);

        Assert.Equal(G, eff[DoorB]);
    }

    [Fact]
    public void MemberPlan_AddedDoor_AddedWhenMissing()
    {
        var existing = new Dictionary<Guid, Guid?>(); // membro sem nenhuma porta
        var plan = GroupAccessService.PlanMemberDoorChange(existing, new[] { DoorB }, Array.Empty<Guid>(), G);

        Assert.Equal(new[] { DoorB }, plan.ToAddInherited);
        Assert.Empty(plan.ToRemoveInherited);
        Assert.True(plan.HardwareChanged);
    }

    [Fact]
    public void MemberPlan_AddedDoor_RetaggedWhenAlreadyManual()
    {
        var existing = new Dictionary<Guid, Guid?> { [DoorB] = null }; // já tinha B como manual
        var plan = GroupAccessService.PlanMemberDoorChange(existing, new[] { DoorB }, Array.Empty<Guid>(), G);

        Assert.Empty(plan.ToAddInherited);
        Assert.Equal(new[] { DoorB }, plan.ToRetagToGroup);
        Assert.False(plan.HardwareChanged); // porta já estava no hardware
    }

    [Fact]
    public void MemberPlan_RemovedDoor_RemovesInheritedButKeepsManual()
    {
        // B veio do grupo (será removida); C é manual (deve permanecer mesmo constando em removed).
        var existing = new Dictionary<Guid, Guid?> { [DoorB] = G, [DoorC] = null };
        var plan = GroupAccessService.PlanMemberDoorChange(existing, Array.Empty<Guid>(), new[] { DoorB, DoorC }, G);

        Assert.Equal(new[] { DoorB }, plan.ToRemoveInherited);
        Assert.DoesNotContain(DoorC, plan.ToRemoveInherited);
        Assert.True(plan.HardwareChanged);
    }

    [Fact]
    public void MemberPlan_RemovedDoor_TaggedByAnotherGroup_NotRemoved()
    {
        var other = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var existing = new Dictionary<Guid, Guid?> { [DoorB] = other };
        var plan = GroupAccessService.PlanMemberDoorChange(existing, Array.Empty<Guid>(), new[] { DoorB }, G);

        Assert.Empty(plan.ToRemoveInherited);
        Assert.False(plan.HardwareChanged);
    }
}
