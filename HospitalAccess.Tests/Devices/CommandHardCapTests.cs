using HospitalAccess.Gateway.Connections;
using Xunit;

namespace HospitalAccess.Tests.Devices;

/// <summary>
/// O teto que decide quanto o sistema aguenta um aparelho mudo. A medição de 43 h mostrou o
/// custo de errar para cima: 290 comandos num teto fixo de 3 min = 14,5 h de espera pura, 64%
/// de todo o tempo de comunicação. Errar para BAIXO tem custo oposto e igualmente real —
/// abandonar uma foto de evento que só precisava de mais alguns segundos. Estes testes fixam
/// os dois lados.
/// </summary>
public class CommandHardCapTests
{
    /// <summary>
    /// Os comandos leves — os que dominam o volume — são o motivo da mudança. Com o padrão do
    /// controlador (3 s × 3 tentativas), o teto cai de 180 s para ~13 s.
    /// </summary>
    [Fact]
    public void Comando_leve_desiste_em_segundos_e_nao_em_minutos()
    {
        var cap = CommandHardCap.For(timeoutMs: 3000, restartCount: 3);

        Assert.Equal(13.5, cap.TotalSeconds, precision: 1);
        Assert.True(cap < TimeSpan.FromSeconds(20), "um ReadWatchState não pode segurar a fila por minutos");
    }

    /// <summary>
    /// O contrapeso: a foto de evento pede 30 s × 3 = 90 s legítimos. Qualquer teto fixo baixo
    /// o bastante para o comando leve mataria esta — é exatamente por isso que o valor é
    /// derivado e não cravado.
    /// </summary>
    [Fact]
    public void Foto_de_evento_mantem_o_tempo_de_que_precisa()
    {
        var cap = CommandHardCap.For(timeoutMs: 30000, restartCount: 3);

        Assert.True(cap.TotalSeconds >= 90,
            $"a foto de evento precisa de 90s legítimos, o teto deu {cap.TotalSeconds}s");
        Assert.True(cap <= CommandHardCap.Max);
    }

    [Fact]
    public void Upload_de_face_fica_entre_os_dois()
    {
        // 15 s de timeout com 1 reenvio no fio (FaceUploadWireRetries = 1).
        var cap = CommandHardCap.For(timeoutMs: 15000, restartCount: 1);

        Assert.Equal(22.5, cap.TotalSeconds, precision: 1);
    }

    /// <summary>Piso: uma oscilação de rede não pode virar falha só porque o timeout é curto.</summary>
    [Theory]
    [InlineData(100, 1)]
    [InlineData(1000, 1)]
    [InlineData(0, 0)]
    [InlineData(-5, -5)]
    public void Nunca_desce_do_piso(int timeoutMs, int restartCount)
    {
        Assert.Equal(CommandHardCap.Min, CommandHardCap.For(timeoutMs, restartCount));
    }

    /// <summary>Teto: uma configuração absurda não pode ressuscitar a espera infinita.</summary>
    [Theory]
    [InlineData(600000, 5)]
    [InlineData(int.MaxValue, 10)]
    public void Nunca_passa_do_teto(int timeoutMs, int restartCount)
    {
        Assert.Equal(CommandHardCap.Max, CommandHardCap.For(timeoutMs, restartCount));
    }

    /// <summary>
    /// A propriedade que importa: o teto SEMPRE cobre o que o comando pede. Se não cobrisse,
    /// o sistema abandonaria comandos que ainda estavam dentro do próprio prazo.
    /// </summary>
    [Theory]
    [InlineData(3000, 3)]
    [InlineData(15000, 1)]
    [InlineData(15000, 3)]
    [InlineData(30000, 3)]
    [InlineData(5000, 2)]
    public void O_teto_cobre_o_que_o_comando_pede(int timeoutMs, int restartCount)
    {
        var pedido = TimeSpan.FromMilliseconds((long)timeoutMs * restartCount);
        var cap = CommandHardCap.For(timeoutMs, restartCount);

        Assert.True(cap >= pedido,
            $"pedido {pedido.TotalSeconds}s mas o teto foi {cap.TotalSeconds}s — abandonaria o comando no prazo");
    }
}
