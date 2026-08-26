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

    /// <summary>
    /// Filtro dos status elegíveis à varredura automática: Pending sempre; Failed só quando o
    /// backoff venceu (NextRetryAtUtc no passado). Failed com NextRetryAtUtc null = quarentena
    /// de falha permanente — fica de fora até uma ação manual resetar.
    /// </summary>
    public static Expression<Func<DeviceSyncStatus, bool>> EligibleForAutoRetry(DateTime nowUtc) =>
        s => s.State == SyncState.Pending
             || (s.State == SyncState.Failed && s.NextRetryAtUtc != null && s.NextRetryAtUtc <= nowUtc);
}
