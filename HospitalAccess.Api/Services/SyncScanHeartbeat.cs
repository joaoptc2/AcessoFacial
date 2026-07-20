namespace HospitalAccess.Api.Services;

/// <summary>
/// Última varredura do <see cref="SyncRetryBackgroundService"/> — prova viva de que o retry
/// automático está rodando. Alimenta a tela de Sincronizações ("o sistema está tentando?"):
/// sem isto o operador vê "N pendentes" no banner e não tem como saber se algo acontece.
/// </summary>
public sealed class SyncScanHeartbeat
{
    /// <summary>Resultado de uma varredura: contagens por categoria e quantos foram reenfileirados.</summary>
    public sealed record Scan(DateTime AtUtc, int Enqueued, int Pending, int DueNow,
        int WaitingBackoff, int Quarantined, DateTime? NextRetryAtUtc);

    // Escrita/leitura de referência é atômica no CLR; sem lock. Null até a primeira varredura.
    public Scan? Last { get; private set; }

    public void Record(Scan scan) => Last = scan;
}
