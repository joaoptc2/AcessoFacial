using HospitalAccess.Api.Services;
using Microsoft.Extensions.Logging;
using Xunit;

namespace HospitalAccess.Tests.Devices;

/// <summary>
/// Trava o comportamento do buffer do modo de desenvolvimento: captura só quando ativo,
/// ids crescentes para busca incremental, ring buffer na capacidade e limpeza.
/// </summary>
public class DevLogBufferTests
{
    [Fact]
    public void Provider_WhenDisabled_CapturesNothing()
    {
        var buffer = new DevLogBuffer();
        var logger = new DevLogLoggerProvider(buffer).CreateLogger("HospitalAccess.Api.Teste");

        logger.LogError("não deve ser capturado");

        Assert.Equal(0, buffer.Count);
        Assert.False(logger.IsEnabled(LogLevel.Critical));
    }

    [Fact]
    public void Provider_WhenEnabled_CapturesAppInfoButNotFrameworkInfo()
    {
        var buffer = new DevLogBuffer();
        buffer.SetEnabled(true);
        var provider = new DevLogLoggerProvider(buffer);
        var appLogger = provider.CreateLogger("HospitalAccess.Api.Services.UserSyncService");
        var frameworkLogger = provider.CreateLogger("Microsoft.EntityFrameworkCore.Database.Command");

        appLogger.LogInformation("sync ok");          // app: Information+ entra
        frameworkLogger.LogInformation("SELECT ...");  // framework: Information é ruído, fica fora
        frameworkLogger.LogWarning("pool esgotado");   // framework: Warning+ entra

        var entries = buffer.Snapshot();
        Assert.Equal(2, entries.Count);
        Assert.Equal("sync ok", entries[0].Message);
        Assert.Equal("pool esgotado", entries[1].Message);
    }

    [Fact]
    public void Snapshot_SinceId_ReturnsOnlyNewerEntries()
    {
        var buffer = new DevLogBuffer();
        buffer.Add("Information", "Teste", "a", null);
        buffer.Add("Information", "Teste", "b", null);
        var firstBatch = buffer.Snapshot();
        buffer.Add("Warning", "Teste", "c", null);

        var incremental = buffer.Snapshot(sinceId: firstBatch[^1].Id);

        Assert.Single(incremental);
        Assert.Equal("c", incremental[0].Message);
        Assert.True(incremental[0].Id > firstBatch[^1].Id); // ids sempre crescentes
    }

    [Fact]
    public void Add_BeyondCapacity_DropsOldest()
    {
        var buffer = new DevLogBuffer();
        for (var i = 0; i < DevLogBuffer.Capacity + 10; i++)
            buffer.Add("Information", "Teste", $"m{i}", null);

        Assert.True(buffer.Count <= DevLogBuffer.Capacity);
        var entries = buffer.Snapshot(take: DevLogBuffer.Capacity);
        Assert.Equal($"m{DevLogBuffer.Capacity + 9}", entries[^1].Message); // mais novas preservadas
    }

    [Fact]
    public void Clear_EmptiesBuffer_ButIdsKeepGrowing()
    {
        var buffer = new DevLogBuffer();
        buffer.Add("Information", "Teste", "antes", null);
        var beforeId = buffer.Snapshot()[0].Id;

        buffer.Clear();
        Assert.Equal(0, buffer.Count);

        buffer.Add("Information", "Teste", "depois", null);
        // Ids não reiniciam após o clear — a busca incremental (sinceId) do front continua válida.
        Assert.True(buffer.Snapshot()[0].Id > beforeId);
    }

    [Fact]
    public void Exception_IsCapturedAsText()
    {
        var buffer = new DevLogBuffer();
        buffer.SetEnabled(true);
        var logger = new DevLogLoggerProvider(buffer).CreateLogger("HospitalAccess.Gateway");

        logger.LogError(new InvalidOperationException("boom"), "falha no comando");

        var entry = Assert.Single(buffer.Snapshot());
        Assert.Equal("falha no comando", entry.Message);
        Assert.Contains("boom", entry.Exception);
    }
}
