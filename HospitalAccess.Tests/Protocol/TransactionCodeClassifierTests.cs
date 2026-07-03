using HospitalAccess.Domain.Enums;
using HospitalAccess.Domain.Protocol;
using Xunit;

namespace HospitalAccess.Tests.Protocol;

public class TransactionCodeClassifierTests
{
    // Faixa 1–14: combinações de verificação válidas → acesso concedido.
    [Theory]
    [InlineData(1)]  // cartão
    [InlineData(2)]  // digital
    [InlineData(3)]  // face
    [InlineData(13)] // Face+Digital+Senha — era erroneamente tratado como negado (regressão B2)
    [InlineData(14)] // Face+Digital+Cartão
    [InlineData(25)] // abertura sem verificação
    [InlineData(29)] // validação OK, validade prestes a expirar
    public void IsAccessGranted_true_para_codigos_validos(int code)
    {
        Assert.True(TransactionCodeClassifier.IsAccessGranted(code));
    }

    // 15 = verificação repetida; 16–30 (exceto 25/29) = negação. 22 = "não abre" (porta travada).
    [Theory]
    [InlineData(15)]
    [InlineData(16)] // fora da validade
    [InlineData(18)] // feriado
    [InlineData(19)] // usuário não registrado
    [InlineData(22)] // verificação com porta travada — este é o "não abre", não o 13
    [InlineData(30)] // temperatura anormal
    public void IsAccessGranted_false_para_negacoes(int code)
    {
        Assert.False(TransactionCodeClassifier.IsAccessGranted(code));
    }

    // Log de sistema (§9.4): 14–20 disparam alarme.
    [Theory]
    [InlineData(14, AlarmKind.InvalidCard)]
    [InlineData(15, AlarmKind.DoorSensor)]
    [InlineData(16, AlarmKind.Duress)]
    [InlineData(17, AlarmKind.OpenDoorTimeout)]
    [InlineData(18, AlarmKind.Blacklist)]
    [InlineData(19, AlarmKind.Fire)]
    [InlineData(20, AlarmKind.AntiTheft)]
    public void TryMapSystemAlarm_mapeia_disparos(int code, AlarmKind expected)
    {
        Assert.True(TransactionCodeClassifier.TryMapSystemAlarm(code, out var kind, out var cleared));
        Assert.Equal(expected, kind);
        Assert.False(cleared);
    }

    // Códigos 21–27 = cancelamento dos alarmes 14–20 (base = código − 7).
    [Theory]
    [InlineData(21, AlarmKind.InvalidCard)]
    [InlineData(22, AlarmKind.DoorSensor)]
    [InlineData(27, AlarmKind.AntiTheft)]
    public void TryMapSystemAlarm_reconhece_cancelamentos(int code, AlarmKind expected)
    {
        Assert.True(TransactionCodeClassifier.TryMapSystemAlarm(code, out var kind, out var cleared));
        Assert.Equal(expected, kind);
        Assert.True(cleared);
    }

    // Outros logs de sistema (ex.: 1 = unlock por software, 28 = sistema ligado) não são alarmes.
    [Theory]
    [InlineData(1)]
    [InlineData(28)]
    [InlineData(35)]
    public void TryMapSystemAlarm_ignora_logs_nao_alarme(int code)
    {
        Assert.False(TransactionCodeClassifier.TryMapSystemAlarm(code, out _, out _));
    }
}
