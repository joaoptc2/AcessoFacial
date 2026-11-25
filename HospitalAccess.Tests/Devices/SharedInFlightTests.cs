using HospitalAccess.Gateway.Connections;
using Xunit;

namespace HospitalAccess.Tests.Devices;

/// <summary>
/// A sonda única por porta. Em nov/2026, sondas paralelas na mesma porta morta deixaram até 6 dos
/// 8 workers da sincronização parados esperando a vez — 2,2 h no período.
/// </summary>
public class SharedInFlightTests
{
    [Fact]
    public async Task Chamadas_simultaneas_na_mesma_chave_executam_uma_vez_e_recebem_o_mesmo_resultado()
    {
        var shared = new SharedInFlight<Guid, bool>();
        var porta = Guid.NewGuid();
        var execucoes = 0;
        var libera = new TaskCompletionSource();

        async Task<bool> Sonda()
        {
            Interlocked.Increment(ref execucoes);
            await libera.Task;
            return true;
        }

        var chamadas = Enumerable.Range(0, 6).Select(_ => shared.RunAsync(porta, Sonda)).ToList();
        Assert.Equal(1, shared.Count);
        libera.SetResult();

        Assert.All(await Task.WhenAll(chamadas), Assert.True);
        Assert.Equal(1, execucoes);
    }

    [Fact]
    public async Task Chaves_diferentes_sao_independentes()
    {
        var shared = new SharedInFlight<Guid, bool>();
        var libera = new TaskCompletionSource();
        var execucoes = 0;

        async Task<bool> Sonda()
        {
            Interlocked.Increment(ref execucoes);
            await libera.Task;
            return true;
        }

        var a = shared.RunAsync(Guid.NewGuid(), Sonda);
        var b = shared.RunAsync(Guid.NewGuid(), Sonda);
        libera.SetResult();
        await Task.WhenAll(a, b);

        Assert.Equal(2, execucoes);
    }

    /// <summary>Não é cache: terminada a sonda, a próxima chamada pergunta à porta de novo.</summary>
    [Fact]
    public async Task Terminada_a_execucao_a_proxima_chamada_executa_de_novo()
    {
        var shared = new SharedInFlight<Guid, int>();
        var porta = Guid.NewGuid();
        var n = 0;

        Assert.Equal(1, await shared.RunAsync(porta, () => Task.FromResult(Interlocked.Increment(ref n))));
        Assert.Equal(2, await shared.RunAsync(porta, () => Task.FromResult(Interlocked.Increment(ref n))));
        Assert.Equal(0, shared.Count);
    }

    [Fact]
    public async Task Falha_chega_a_todos_e_libera_a_chave()
    {
        var shared = new SharedInFlight<Guid, bool>();
        var porta = Guid.NewGuid();
        var libera = new TaskCompletionSource();

        async Task<bool> Quebra()
        {
            await libera.Task;
            throw new InvalidOperationException("porta caiu");
        }

        var chamadas = Enumerable.Range(0, 3).Select(_ => shared.RunAsync(porta, Quebra)).ToList();
        libera.SetResult();

        foreach (var chamada in chamadas)
            await Assert.ThrowsAsync<InvalidOperationException>(() => chamada);
        Assert.Equal(0, shared.Count);
        Assert.True(await shared.RunAsync(porta, () => Task.FromResult(true)));
    }

    /// <summary>Quem desiste de esperar não cancela a sonda dos outros.</summary>
    [Fact]
    public async Task Cancelar_a_espera_de_um_nao_interrompe_a_execucao_compartilhada()
    {
        var shared = new SharedInFlight<Guid, bool>();
        var porta = Guid.NewGuid();
        var libera = new TaskCompletionSource();
        using var desiste = new CancellationTokenSource();

        async Task<bool> Sonda()
        {
            await libera.Task;
            return true;
        }

        var dono = shared.RunAsync(porta, Sonda);
        var impaciente = shared.RunAsync(porta, Sonda, desiste.Token);
        desiste.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => impaciente);
        libera.SetResult();
        Assert.True(await dono);
    }
}
