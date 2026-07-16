using HospitalAccess.Api.Services;
using Xunit;

namespace HospitalAccess.Tests.Access;

/// <summary>
/// Trava a guarda de reentrância das operações pesadas (resync-all, sync-all de feriados/grades):
/// segunda tentativa com a mesma chave é negada até o End, chaves diferentes são independentes.
/// </summary>
public class SingleFlightTests
{
    [Fact]
    public void TryBegin_SecondCallSameKey_IsDenied()
    {
        var sf = new SingleFlight();
        Assert.True(sf.TryBegin("resync:abc"));
        Assert.False(sf.TryBegin("resync:abc"));
    }

    [Fact]
    public void End_ReleasesKey()
    {
        var sf = new SingleFlight();
        Assert.True(sf.TryBegin("holidays:sync-all"));
        sf.End("holidays:sync-all");
        Assert.True(sf.TryBegin("holidays:sync-all"));
    }

    [Fact]
    public void DistinctKeys_AreIndependent()
    {
        var sf = new SingleFlight();
        Assert.True(sf.TryBegin("resync:a"));
        Assert.True(sf.TryBegin("resync:b"));
        Assert.False(sf.TryBegin("resync:a"));
    }

    [Fact]
    public void End_UnknownKey_IsNoOp()
    {
        var sf = new SingleFlight();
        sf.End("nunca-iniciada"); // não lança
        Assert.True(sf.TryBegin("nunca-iniciada"));
    }
}
