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
/// folga para a rede. Derivar em vez de cravar é o que permite ser agressivo sem estrangular
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

    public static TimeSpan For(int timeoutMs, int restartCount)
    {
        var pedido = (long)Math.Max(1, timeoutMs) * Math.Max(1, restartCount);
        var comFolga = TimeSpan.FromMilliseconds(pedido * SafetyFactor);
        return comFolga < Min ? Min : comFolga > Max ? Max : comFolga;
    }
}
