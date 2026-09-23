using HospitalAccess.Application.Diagnostics;
using Xunit;

namespace HospitalAccess.Tests.Access;

/// <summary>
/// A origem do comando é o que separa "o operador pediu e demorou" de "a rotina de fundo estava
/// passando na mesma hora". Se ela vazar entre fluxos, o CSV inteiro mente — e mente de um jeito
/// que não dá para perceber olhando os números.
/// </summary>
public class DeviceTraceContextTests
{
    [Fact]
    public void Sem_marcacao_a_origem_e_desconhecida()
    {
        Assert.Equal(DeviceTraceTrigger.Unknown, DeviceTraceContext.Trigger);
    }

    [Fact]
    public void Dentro_do_escopo_vale_a_origem_marcada()
    {
        using (DeviceTraceContext.Use(DeviceTraceTrigger.SyncQueue))
            Assert.Equal(DeviceTraceTrigger.SyncQueue, DeviceTraceContext.Trigger);
    }

    [Fact]
    public void Ao_sair_do_escopo_a_origem_volta_ao_que_era()
    {
        using (DeviceTraceContext.Use(DeviceTraceTrigger.SyncQueue))
        {
            using (DeviceTraceContext.Use(DeviceTraceTrigger.ManualCommand))
                Assert.Equal(DeviceTraceTrigger.ManualCommand, DeviceTraceContext.Trigger);

            // Aninhado restaura o de fora, não zera: um comando manual disparado de dentro de um
            // fluxo de fila não pode deixar o resto da fila marcado como manual.
            Assert.Equal(DeviceTraceTrigger.SyncQueue, DeviceTraceContext.Trigger);
        }

        Assert.Equal(DeviceTraceTrigger.Unknown, DeviceTraceContext.Trigger);
    }

    /// <summary>
    /// O caso que justifica o AsyncLocal: 8 workers de sincronização rodam ao mesmo tempo, e
    /// cada um precisa carregar a SUA origem. Uma variável estática comum embaralharia tudo.
    /// </summary>
    [Fact]
    public async Task Fluxos_paralelos_nao_contaminam_a_origem_um_do_outro()
    {
        var origens = new[]
        {
            DeviceTraceTrigger.SyncQueue,
            DeviceTraceTrigger.ManualCommand,
            DeviceTraceTrigger.HealthCheck,
            DeviceTraceTrigger.BedManagement,
        };

        var tarefas = origens.Select(origem => Task.Run(async () =>
        {
            using var scope = DeviceTraceContext.Use(origem);
            // O await é o ponto onde uma implementação ingênua perderia (ou trocaria) o valor.
            await Task.Delay(Random.Shared.Next(5, 30));
            return (Esperado: origem, Lido: DeviceTraceContext.Trigger);
        }));

        foreach (var (esperado, lido) in await Task.WhenAll(tarefas))
            Assert.Equal(esperado, lido);
    }

    [Fact]
    public async Task A_origem_atravessa_o_await()
    {
        using var scope = DeviceTraceContext.Use(DeviceTraceTrigger.BedManagement);
        await Task.Yield();
        await Task.Delay(10);
        Assert.Equal(DeviceTraceTrigger.BedManagement, DeviceTraceContext.Trigger);
    }

    /// <summary>Desligado = nada é gravado, e o Record nunca lança.</summary>
    [Fact]
    public void Destino_nulo_aceita_tudo_sem_efeito()
    {
        var sink = new NullDeviceTraceSink();
        Assert.False(sink.Enabled);
        sink.Record(new DeviceCommandTraceRecord(
            DateTime.UtcNow, Guid.NewGuid(), "Porta", "10.0.0.1", "SDK", "AddPerson",
            DeviceTraceTrigger.SyncQueue, 0, 0, 0, DeviceTraceOutcome.Success, null, 0, 3000, 3, null));
    }
}
