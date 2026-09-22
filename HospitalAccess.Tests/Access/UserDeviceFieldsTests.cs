using HospitalAccess.Api.Services;
using HospitalAccess.Domain.Entities;
using Xunit;

namespace HospitalAccess.Tests.Access;

/// <summary>
/// Trava o diff que decide se uma edição de usuário gera tráfego ao hardware: só os campos
/// realmente gravados no aparelho (nome, cartão, foto) disparam re-envio — edição de perfil
/// administrativo não pode custar re-upload de face.
///
/// <para>
/// A grade de horário saiu deste diff porque deixou de ser do usuário: ela virou POR PORTA
/// (AccessPermission.TimeGroup) e é aplicada pelo serviço de grades, que já reescreve a tabela
/// do aparelho e reenfileira a pessoa quando muda. Mantê-la aqui dispararia re-upload de face
/// por uma mudança que não tem nada a ver com a face.
/// </para>
/// </summary>
public class UserDeviceFieldsTests
{
    private static User BaseUser() => new()
    {
        Name = "Maria Silva",
        CardNumber = 1234,
    };

    [Fact]
    public void Changed_NenhumCampoDoAparelho_NaoDisparaReenvio()
    {
        var user = BaseUser();
        // Mesmos Name/CardNumber, sem foto nova: campos administrativos (telefone, e-mail,
        // cargo, notas, grupo organizacional) não entram no diff de propósito.
        Assert.False(UserDeviceFields.Changed(user, "Maria Silva", 1234, hasNewPhoto: false));
    }

    [Fact]
    public void Changed_Nome_Dispara() =>
        Assert.True(UserDeviceFields.Changed(BaseUser(), "Maria S. Costa", 1234, hasNewPhoto: false));

    [Fact]
    public void Changed_Cartao_Dispara() =>
        Assert.True(UserDeviceFields.Changed(BaseUser(), "Maria Silva", 9999, hasNewPhoto: false));

    [Fact]
    public void Changed_CartaoRemovido_Dispara() =>
        Assert.True(UserDeviceFields.Changed(BaseUser(), "Maria Silva", null, hasNewPhoto: false));

    [Fact]
    public void Changed_FotoNova_Dispara() =>
        Assert.True(UserDeviceFields.Changed(BaseUser(), "Maria Silva", 1234, hasNewPhoto: true));
}
