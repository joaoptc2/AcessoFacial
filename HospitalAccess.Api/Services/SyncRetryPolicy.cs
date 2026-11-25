using System.Linq.Expressions;
using HospitalAccess.Api.Options;
using HospitalAccess.Domain.Entities;
using HospitalAccess.Domain.Enums;
using HospitalAccess.Gateway;

namespace HospitalAccess.Api.Services;

/// <summary>
/// Política de retry automático da sincronização por porta. Duas classes de falha:
/// - PERMANENTE (foto sem rosto, feature code ilegível, duplicidade): re-tentar com os mesmos
///   dados nunca terá sucesso — sai do retry automático (NextRetryAtUtc = null) e só volta por
///   ação manual (nova foto, resync, resolução de conflito).
/// - TRANSITÓRIA (aparelho offline, timeout, CRC): re-tenta com backoff exponencial até o teto —
///   sem isso, cada usuário falho re-enviava a face completa (~120 KB) a cada varredura de 2 min,
///   para sempre, saturando a rede dos controladores.
/// </summary>
public static class SyncRetryPolicy
{
    /// <summary>Falha que nunca terá sucesso com os mesmos dados (não elegível a retry automático).</summary>
    public static bool IsPermanent(FaceUploadCode code) =>
        code is FaceUploadCode.NoFaceInPhoto
             or FaceUploadCode.FeatureCodeUnidentifiable
             or FaceUploadCode.Duplicate
             // Foto que nem converte (corrompida/formato não suportado) é falha do DADO: reenviar
             // o mesmo arquivo é garantia de repetir o erro, então vai para quarentena também.
             or FaceUploadCode.InvalidImage;

    /// <summary>
    /// Espera antes da próxima tentativa após <paramref name="retryCount"/> falhas consecutivas.
    /// Com os defaults (base 2 min, fator 2, teto 60 min): 2, 4, 8, 16, 32, 60, 60, ...
    /// </summary>
    public static TimeSpan Backoff(int retryCount, SyncRetryOptions options) =>
        TimeSpan.FromMinutes(Math.Min(
            options.BackoffBaseMinutes * Math.Pow(options.BackoffFactor, Math.Max(0, retryCount - 1)),
            options.BackoffCapMinutes));

    /// <summary>Folga mínima depois que a porta volta a aceitar comandos.</summary>
    public static readonly TimeSpan UnhealthyPortMinDelay = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Janela sobre a qual as novas tentativas adiadas se espalham. Cinco minutos é o ciclo do
    /// monitoramento: dentro dela, a sonda barata dele já terá dito se a porta voltou.
    /// </summary>
    public static readonly TimeSpan UnhealthyPortJitter = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Quando tentar de novo um cadastro adiado por porta sem condições: depois da reabertura do
    /// circuito (ou de agora, se ele não está aberto), mais a folga, mais um sorteio na janela.
    ///
    /// O sorteio é o conserto de um defeito medido. A versão anterior marcava TODOS os usuários
    /// para "reabertura + 1 min" — o mesmo instante —, então eles chegavam juntos e faziam fila
    /// na porta: em nov/2026, 142 rajadas de até sete uploads de face no Quarto 9, 17,2 h numa
    /// semana. Espalhados, eles chegam um a um, e o primeiro já diz aos outros se vale tentar.
    /// </summary>
    public static DateTime DeferForUnhealthyPort(DateTime nowUtc, DateTime? circuitOpenUntilUtc, Random random)
    {
        var desde = circuitOpenUntilUtc is { } ate && ate > nowUtc ? ate : nowUtc;
        var sorteio = TimeSpan.FromMilliseconds(random.NextDouble() * UnhealthyPortJitter.TotalMilliseconds);
        return desde + UnhealthyPortMinDelay + sorteio;
    }

    /// <summary>
    /// De quanto em quanto tempo um cadastro adiado por porta EM MANUTENÇÃO é reavaliado. A saída
    /// da manutenção já reenfileira tudo na hora (ControllerMaintenanceService); este prazo é só a
    /// rede de segurança, e a reavaliação não toca o aparelho — é uma consulta ao banco.
    /// </summary>
    public static readonly TimeSpan MaintenanceRecheck = TimeSpan.FromHours(6);

    /// <summary>
    /// Filtro dos status elegíveis à varredura automática: Pending sempre; Failed só quando o
    /// backoff venceu (NextRetryAtUtc no passado). Failed com NextRetryAtUtc null = quarentena
    /// de falha permanente — fica de fora até uma ação manual resetar.
    /// </summary>
    public static Expression<Func<DeviceSyncStatus, bool>> EligibleForAutoRetry(DateTime nowUtc) =>
        s => s.State == SyncState.Pending
             || (s.State == SyncState.Failed && s.NextRetryAtUtc != null && s.NextRetryAtUtc <= nowUtc);
}
