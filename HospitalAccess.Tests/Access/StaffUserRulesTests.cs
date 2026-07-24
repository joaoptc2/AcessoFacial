using HospitalAccess.Api.Services;
using HospitalAccess.Domain.Enums;
using Xunit;

namespace HospitalAccess.Tests.Access;

public class StaffUserRulesTests
{
    [Fact]
    public void Ultimo_admin_ativo_nao_pode_ser_rebaixado()
    {
        var ok = StaffUserRules.CanChange(StaffRole.Admin, currentlyActive: true,
            StaffRole.Operator, newActive: true, isLastActiveAdmin: true, out var reason);

        Assert.False(ok);
        Assert.Contains("único Admin", reason);
    }

    [Fact]
    public void Ultimo_admin_ativo_nao_pode_ser_desativado()
    {
        var ok = StaffUserRules.CanChange(StaffRole.Admin, currentlyActive: true,
            StaffRole.Admin, newActive: false, isLastActiveAdmin: true, out _);

        Assert.False(ok);
    }

    [Fact]
    public void Admin_pode_ser_rebaixado_quando_ha_outro_admin_ativo()
    {
        var ok = StaffUserRules.CanChange(StaffRole.Admin, currentlyActive: true,
            StaffRole.Reception, newActive: true, isLastActiveAdmin: false, out var reason);

        Assert.True(ok);
        Assert.Null(reason);
    }

    [Fact]
    public void Nao_admin_pode_mudar_livremente()
    {
        // isLastActiveAdmin nunca é true para não-Admin, mas a regra não pode depender disso.
        var ok = StaffUserRules.CanChange(StaffRole.Reception, currentlyActive: true,
            StaffRole.Operator, newActive: false, isLastActiveAdmin: false, out _);

        Assert.True(ok);
    }

    [Fact]
    public void Admin_inativo_pode_ser_alterado_mesmo_sendo_o_unico()
    {
        // Já está inativo — não "perde" um Admin ativo; reativar/promover deve sempre passar.
        var ok = StaffUserRules.CanChange(StaffRole.Admin, currentlyActive: false,
            StaffRole.Admin, newActive: true, isLastActiveAdmin: false, out _);

        Assert.True(ok);
    }

    [Theory]
    [InlineData(true, StaffRole.Admin, StaffRole.Operator, true, true)]   // se rebaixando
    [InlineData(true, StaffRole.Admin, StaffRole.Admin, false, true)]     // se desativando
    [InlineData(true, StaffRole.Admin, StaffRole.Admin, true, false)]     // sem mudança perigosa
    [InlineData(true, StaffRole.Operator, StaffRole.Reception, true, false)] // não-admin pode
    [InlineData(false, StaffRole.Admin, StaffRole.Operator, true, false)] // outro usuário → regra do último admin decide
    public void Autoexclusao_de_admin_e_bloqueada(bool isSelf, StaffRole current, StaffRole next, bool nextActive, bool expected)
    {
        Assert.Equal(expected, StaffUserRules.IsSelfLockout(isSelf, current, next, nextActive));
    }
}
