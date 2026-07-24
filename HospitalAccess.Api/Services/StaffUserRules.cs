using HospitalAccess.Domain.Enums;

namespace HospitalAccess.Api.Services;

/// <summary>
/// Guardas puras da gestão de usuários do sistema (testáveis): o sistema nunca pode ficar sem
/// um Admin ativo, e ninguém se tranca para fora sozinho.
/// </summary>
public static class StaffUserRules
{
    /// <summary>
    /// A mudança (novo cargo/ativo) pode ser aplicada? <paramref name="isLastActiveAdmin"/> =
    /// o alvo é o ÚNICO Admin ativo do sistema.
    /// </summary>
    public static bool CanChange(StaffRole currentRole, bool currentlyActive,
        StaffRole newRole, bool newActive, bool isLastActiveAdmin, out string? reason)
    {
        var losesAdmin = currentRole == StaffRole.Admin && currentlyActive
                         && (newRole != StaffRole.Admin || !newActive);
        if (losesAdmin && isLastActiveAdmin)
        {
            reason = "Este é o único Admin ativo do sistema — promova outro usuário a Admin antes de rebaixá-lo ou desativá-lo.";
            return false;
        }
        reason = null;
        return true;
    }

    /// <summary>O próprio usuário logado não pode se rebaixar de Admin nem se desativar (trancar-se para fora).</summary>
    public static bool IsSelfLockout(bool isSelf, StaffRole currentRole, StaffRole newRole, bool newActive) =>
        isSelf && currentRole == StaffRole.Admin && (newRole != StaffRole.Admin || !newActive);
}
