namespace HospitalAccess.Api.Services;

/// <summary>
/// Snapshot (preenchido UMA vez no boot) das migrations pendentes no banco. O app NÃO aplica
/// migrations sozinho — o fluxo manual está em docs/instalacao-servidor-linux.md §13 — então
/// quando o binário novo sobe na frente do banco, este estado alimenta o erro destacado do
/// startup e a faixa vermelha do painel, em vez de deixar as queries quebrarem em runtime com
/// um 42703 sem explicação.
/// </summary>
public sealed class DatabaseSchemaState
{
    public IReadOnlyList<string> PendingMigrations { get; private set; } = Array.Empty<string>();

    public void SetPending(IReadOnlyList<string> migrations) => PendingMigrations = migrations;
}
