namespace HospitalAccess.Gateway.Connections;

/// <summary>
/// Quanto o sistema espera por um comando antes de abandoná-lo.
///
/// Nasceu de uma medição: 43 h em 24 leitoras mostraram 290 comandos batendo um teto FIXO de
/// 3 minutos, somando 14,5 h — 64% de todo o tempo de comunicação do período, gasto esperando
/// aparelhos que não iam responder. O teto fixo era dimensionado pelo comando mais pesado (foto
/// de evento, 30 s × 3 tentativas), então um ReadWatchState de 148 ms segurava a fila por 180 s.
///
/// A regra: cada comando espera o que ELE pede (timeout × tentativas) mais metade disso de
/// folga para a rede — ou, se carrega bytes, o tempo de transferi-los à vazão mínima, o que
/// for maior. Derivar em vez de cravar é o que permite ser agressivo sem estrangular
/// ninguém — qualquer número fixo baixo o bastante para o ReadWatchState mataria a foto.
/// </summary>
public static class CommandHardCap
{
    /// <summary>Piso: abaixo disso, uma oscilação de rede viraria falha.</summary>
    public static readonly TimeSpan Min = TimeSpan.FromSeconds(10);

    /// <summary>Teto: o valor antigo, agora só como limite superior do que é derivado.</summary>
    public static readonly TimeSpan Max = TimeSpan.FromMinutes(3);

    /// <summary>Folga sobre o que o comando pede, para a rede e o processamento do aparelho.</summary>
    public const double SafetyFactor = 1.5;

    /// <summary>
    /// Vazão mínima que ainda conta como "transferindo": 2 KB/s, quatro vezes abaixo da mediana
    /// medida nos uploads de face que deram certo (~7,8 KB/s). Abaixo disso a porta está
    /// inutilizável de qualquer jeito, e o próprio timeout por etapa do SDK já desiste.
    /// </summary>
    public const int MinTransferBytesPerSecond = 2048;

    /// <param name="payloadBytes">
    /// Bytes que o comando envia (0 = comando sem carga). O timeout do SDK vale por ETAPA, e um
    /// upload grande vai em várias etapas; então "timeout × tentativas" subestima o tempo legítimo
    /// dele. Em nov/2026, com o teto só por timeout (15 s × 1 × 1,5 = 22,5 s), 687 uploads de face
    /// para portas VIVAS foram abandonados no teto com o SDK ainda transferindo — e reenviados
    /// do zero depois. Com a carga no cálculo, ~104 KB ganham ~52 s.
    /// </param>
    public static TimeSpan For(int timeoutMs, int restartCount, int payloadBytes = 0)
    {
        var pedido = (long)Math.Max(1, timeoutMs) * Math.Max(1, restartCount);
        var comFolga = TimeSpan.FromMilliseconds(pedido * SafetyFactor);
        var transferencia = TimeSpan.FromSeconds((double)Math.Max(0, payloadBytes) / MinTransferBytesPerSecond);
        var cap = comFolga > transferencia ? comFolga : transferencia;
        return cap < Min ? Min : cap > Max ? Max : cap;
    }
}
