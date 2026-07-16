using HospitalAccess.Api.Services;
using HospitalAccess.Domain.Entities;
using Xunit;

namespace HospitalAccess.Tests.Access;

/// <summary>
/// Trava o diff que decide se uma edição de usuário gera tráfego ao hardware: só os campos
/// realmente gravados no aparelho (nome, grupo de horário, cartão, foto) disparam re-envio —
/// edição de perfil administrativo não pode custar re-upload de face.
/// </summary>
public class UserDeviceFieldsTests
{
    private static User BaseUser() => new()
    {
        Name = "Maria Silva",
        TimeGroup = 1,
        CardNumber = 1234,
    };

    [Fact]
    public void Changed_NoDeviceFieldChanged_ReturnsFalse()
    {
        var user = BaseUser();
        // Mesmos Name/TimeGroup/CardNumber, sem foto nova: campos administrativos (telefone,
        // e-mail, cargo, notas, grupo organizacional) não entram no diff de propósito.
        Assert.False(UserDeviceFields.Changed(user, "Maria Silva", 1, 1234, hasNewPhoto: false));
    }

    [Fact]
    public void Changed_Name_Triggers() =>
        Assert.True(UserDeviceFields.Changed(BaseUser(), "Maria S. Costa", 1, 1234, hasNewPhoto: false));

    [Fact]
    public void Changed_TimeGroup_Triggers() =>
        Assert.True(UserDeviceFields.Changed(BaseUser(), "Maria Silva", 2, 1234, hasNewPhoto: false));

    [Fact]
    public void Changed_CardNumber_Triggers() =>
        Assert.True(UserDeviceFields.Changed(BaseUser(), "Maria Silva", 1, 9999, hasNewPhoto: false));

    [Fact]
    public void Changed_CardNumberCleared_Triggers() =>
        Assert.True(UserDeviceFields.Changed(BaseUser(), "Maria Silva", 1, null, hasNewPhoto: false));

    [Fact]
    public void Changed_NewPhoto_Triggers() =>
        Assert.True(UserDeviceFields.Changed(BaseUser(), "Maria Silva", 1, 1234, hasNewPhoto: true));
}
