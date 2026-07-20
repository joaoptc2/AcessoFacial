import { useEffect, useRef, useState } from "react";
import { Link } from "react-router-dom";
import {
  api,
  ApiError,
  type SyncOverviewDto,
  type SyncPendingCategory,
  type SyncPendingItemDto,
} from "../lib/api";
import { useAuth } from "../lib/AuthContext";

const POLL_INTERVAL_MS = 5000;

const CATEGORY_LABEL: Record<SyncPendingCategory, string> = {
  Pending: "Pendente",
  DueNow: "Elegível agora",
  WaitingBackoff: "Backoff",
  Quarantined: "Quarentena",
  Conflict: "Conflito",
};

const CATEGORY_PILL: Record<SyncPendingCategory, string> = {
  Pending: "pill pill-warning",
  DueNow: "pill pill-warning",
  WaitingBackoff: "pill",
  Quarantined: "pill pill-danger",
  Conflict: "pill pill-danger",
};

function fmtRelative(iso: string | null, nowMs: number): string {
  if (!iso) return "—";
  const diffMs = new Date(iso).getTime() - nowMs;
  const abs = Math.abs(diffMs);
  const prefix = diffMs >= 0 ? "em " : "há ";
  if (abs < 45_000) return diffMs >= 0 ? "agora" : "agora mesmo";
  if (abs < 3_600_000) return `${prefix}${Math.round(abs / 60_000)} min`;
  if (abs < 86_400_000) return `${prefix}${Math.round(abs / 3_600_000)} h`;
  return `${prefix}${Math.round(abs / 86_400_000)} d`;
}

function fmtTime(iso: string): string {
  return new Date(iso).toLocaleTimeString();
}

function truncate(text: string, max: number): string {
  return text.length > max ? `${text.slice(0, max)}…` : text;
}

/**
 * Tela "Sincronizações": TODAS as pendências usuário×porta com o status real (backoff,
 * quarentena, conflito), o estado vivo da fila ("na fila"/"sincronizando agora") e o
 * heartbeat da varredura automática — responde "o sistema está de fato tentando?".
 * Botões de forçar reusam os endpoints existentes (Admin/Operator).
 */
export function SyncPage() {
  const { role } = useAuth();
  const canEdit = role === "Admin" || role === "Operator";

  const [overview, setOverview] = useState<SyncOverviewDto | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [busyUserId, setBusyUserId] = useState<string | null>(null);
  const [busyAll, setBusyAll] = useState(false);
  const [categoryFilter, setCategoryFilter] = useState<"" | SyncPendingCategory>("");
  const loadingRef = useRef(false);

  async function fetchOverview() {
    if (loadingRef.current) return;
    loadingRef.current = true;
    try {
      setOverview(await api.getSyncOverview());
      setError(null);
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Falha ao carregar as sincronizações.");
    } finally {
      loadingRef.current = false;
    }
  }

  useEffect(() => {
    fetchOverview();
    const timer = setInterval(fetchOverview, POLL_INTERVAL_MS);
    return () => clearInterval(timer);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  async function forceAll() {
    setBusyAll(true);
    setNotice(null);
    try {
      const result = await api.syncFailedUsers();
      setNotice(`${result.enqueued} usuário(s) reenfileirado(s) para sincronização imediata.`);
      setError(null);
      await fetchOverview();
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Falha ao forçar as sincronizações.");
    } finally {
      setBusyAll(false);
    }
  }

  async function forceUser(item: SyncPendingItemDto) {
    setBusyUserId(item.userId);
    setNotice(null);
    try {
      await api.resyncUser(item.userId);
      setNotice(`Sincronização de "${item.userName}" reenfileirada (todas as portas pendentes).`);
      setError(null);
      await fetchOverview();
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Falha ao forçar a sincronização.");
    } finally {
      setBusyUserId(null);
    }
  }

  async function resolveConflict(item: SyncPendingItemDto, replace: boolean) {
    const question = replace
      ? `Substituir o cadastro existente no aparelho (código ${item.conflictUserCode}) pela face de "${item.userName}"?`
      : `Manter o cadastro existente no aparelho e REMOVER a permissão de "${item.userName}" nesta porta?`;
    if (!window.confirm(question)) return;
    setBusyUserId(item.userId);
    setNotice(null);
    try {
      if (replace) await api.resolveConflictReplace(item.userId, item.controllerId);
      else await api.resolveConflictKeepExisting(item.userId, item.controllerId);
      setNotice("Conflito encaminhado para resolução.");
      setError(null);
      await fetchOverview();
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Falha ao resolver o conflito.");
    } finally {
      setBusyUserId(null);
    }
  }

  const nowMs = overview ? new Date(overview.generatedAtUtc).getTime() : Date.now();
  const scan = overview?.scan ?? null;
  const scanLate = scan !== null && nowMs - new Date(scan.lastScanAtUtc).getTime() > 2 * scan.scanIntervalSeconds * 1000;
  const items = overview?.items ?? [];
  const filtered = categoryFilter ? items.filter((i) => i.category === categoryFilter) : items;

  const summaryCards: { key: SyncPendingCategory; label: string; value: number; hint: string }[] = overview
    ? [
        { key: "Pending", label: "Pendentes", value: overview.totals.pending, hint: "aguardando a fila/varredura" },
        { key: "DueNow", label: "Elegíveis agora", value: overview.totals.dueNow, hint: "backoff vencido — próxima varredura reenfileira" },
        { key: "WaitingBackoff", label: "Aguardando backoff", value: overview.totals.waitingBackoff, hint: "re-tentativa automática agendada" },
        { key: "Quarantined", label: "Quarentena", value: overview.totals.quarantined, hint: "erro permanente — só sai por ação manual" },
        { key: "Conflict", label: "Conflitos", value: overview.totals.conflicts, hint: "face duplicada — substituir ou manter" },
      ]
    : [];

  return (
    <div>
      <h2>Sincronizações</h2>
      <p className="text-muted">
        Todas as pendências de sincronização com os controladores, o que está na fila neste
        instante e a última varredura automática. "Forçar agora" zera o backoff/quarentena do
        usuário e o coloca imediatamente na fila.
      </p>

      {error && <div className="alert alert-danger">{error}</div>}
      {notice && <div className="alert alert-success">{notice}</div>}

      <div className="card" style={{ marginBottom: "1rem", display: "flex", gap: "0.75rem", alignItems: "center", flexWrap: "wrap" }}>
        {scan === null ? (
          <span className="text-muted">
            Aguardando a primeira varredura automática do retry (acontece logo após o início do serviço).
          </span>
        ) : (
          <>
            <span>
              Última varredura: <strong>{fmtRelative(scan.lastScanAtUtc, nowMs)}</strong>{" "}
              ({scan.lastEnqueued} reenfileirado(s)) · próxima ~{fmtTime(scan.nextScanAtUtc)}
              {scan.nextRetryAtUtc && <> · próximo backoff vence às {fmtTime(scan.nextRetryAtUtc)}</>}
            </span>
            {scanLate && <span className="pill pill-danger">varredura atrasada — verifique o serviço</span>}
          </>
        )}
        {overview && (
          <span style={{ marginLeft: "auto" }}>
            Na fila: <strong>{overview.queuedUsers}</strong> · Sincronizando agora:{" "}
            <strong>{overview.processing.length}</strong>
            {overview.processing.length > 0 && (
              <span className="text-muted">
                {" "}
                ({overview.processing
                  .slice(0, 3)
                  .map((p) => p.userName || p.userId.slice(0, 8))
                  .join(", ")}
                {overview.processing.length > 3 ? "…" : ""})
              </span>
            )}
          </span>
        )}
      </div>

      {overview && (
        <div style={{ display: "flex", gap: "0.75rem", flexWrap: "wrap", marginBottom: "1rem" }}>
          {summaryCards.map((c) => (
            <button
              key={c.key}
              type="button"
              className="card"
              title={c.hint}
              onClick={() => setCategoryFilter(categoryFilter === c.key ? "" : c.key)}
              style={{
                cursor: "pointer",
                minWidth: 140,
                textAlign: "left",
                border: categoryFilter === c.key ? "2px solid var(--primary, #2563eb)" : undefined,
              }}
            >
              <div style={{ fontSize: "1.4rem", fontWeight: 700 }}>{c.value}</div>
              <div className="text-muted" style={{ fontSize: "0.85rem" }}>{c.label}</div>
            </button>
          ))}
          {canEdit && (
            <div style={{ display: "flex", alignItems: "center", marginLeft: "auto" }}>
              <button className="btn btn-primary" disabled={busyAll} onClick={forceAll}>
                Forçar todas agora
              </button>
            </div>
          )}
        </div>
      )}

      {overview && overview.totalItems === 0 ? (
        <div className="card text-muted">
          Nenhuma sincronização pendente — todos os cadastros estão em dia nos controladores.
        </div>
      ) : (
        <>
          {categoryFilter && (
            <p className="text-muted" style={{ marginBottom: "0.5rem" }}>
              Filtro: {CATEGORY_LABEL[categoryFilter]} ({filtered.length}) ·{" "}
              <button className="btn btn-sm btn-outline" onClick={() => setCategoryFilter("")}>
                limpar filtro
              </button>
            </p>
          )}
          {overview && overview.totalItems > overview.items.length && (
            <div className="alert" style={{ background: "#fffbeb", color: "#92400e", border: "1px solid #fde68a" }}>
              Exibindo {overview.items.length} de {overview.totalItems} pendências.
            </div>
          )}
          <table>
            <thead>
              <tr>
                <th>Usuário</th>
                <th>Porta</th>
                <th>Ação pendente</th>
                <th>Situação</th>
                <th>Próxima tentativa</th>
                <th>Tentativas</th>
                <th>Último erro</th>
                <th>Atualizado</th>
                {canEdit && <th>Ações</th>}
              </tr>
            </thead>
            <tbody>
              {filtered.map((item) => (
                <tr key={`${item.userId}:${item.controllerId}`}>
                  <td>
                    {item.userType === "Permanent" ? (
                      <Link to={`/users/${item.userId}`}>{item.userName}</Link>
                    ) : (
                      item.userName
                    )}{" "}
                    {item.userType === "Visitor" && <span className="pill">Visitante</span>}{" "}
                    {item.userRevoked && <span className="pill">Revogado</span>}
                  </td>
                  <td>
                    {item.controllerName}{" "}
                    {!item.controllerOnline && <span className="pill pill-danger">offline</span>}{" "}
                    {item.circuitOpenUntilUtc && (
                      <span
                        className="pill pill-warning"
                        title="Protocolo sem resposta — o disjuntor pausa os comandos para proteger o aparelho."
                      >
                        protegido até {fmtTime(item.circuitOpenUntilUtc)}
                      </span>
                    )}
                  </td>
                  <td>{item.action === "Send" ? "Enviar cadastro" : "Remover da porta"}</td>
                  <td>
                    {item.processingSinceUtc ? (
                      <span className="pill pill-success">Sincronizando…</span>
                    ) : item.inQueue ? (
                      <span className="pill">Na fila</span>
                    ) : (
                      <span className={CATEGORY_PILL[item.category]}>{CATEGORY_LABEL[item.category]}</span>
                    )}
                  </td>
                  <td style={{ whiteSpace: "nowrap" }}>
                    {item.processingSinceUtc || item.inQueue
                      ? "em andamento"
                      : item.category === "Quarantined" || item.category === "Conflict"
                        ? "manual"
                        : item.category === "Pending" || item.category === "DueNow"
                          ? "próxima varredura"
                          : fmtRelative(item.nextRetryAtUtc, nowMs)}
                  </td>
                  <td>{item.retryCount}</td>
                  <td title={item.lastError ?? undefined} style={{ maxWidth: 280, wordBreak: "break-word" }}>
                    {item.lastError ? truncate(item.lastError, 80) : "—"}
                  </td>
                  <td style={{ whiteSpace: "nowrap" }}>{fmtRelative(item.updatedAtUtc, nowMs)}</td>
                  {canEdit && (
                    <td style={{ whiteSpace: "nowrap" }}>
                      {item.category === "Conflict" ? (
                        <>
                          <button
                            className="btn btn-sm btn-primary"
                            disabled={busyUserId === item.userId}
                            onClick={() => resolveConflict(item, true)}
                          >
                            Substituir
                          </button>{" "}
                          <button
                            className="btn btn-sm btn-outline"
                            disabled={busyUserId === item.userId}
                            onClick={() => resolveConflict(item, false)}
                          >
                            Manter existente
                          </button>
                        </>
                      ) : (
                        <button
                          className="btn btn-sm btn-primary"
                          disabled={busyUserId === item.userId || item.processingSinceUtc !== null}
                          onClick={() => forceUser(item)}
                        >
                          Forçar agora
                        </button>
                      )}
                    </td>
                  )}
                </tr>
              ))}
            </tbody>
          </table>
        </>
      )}
    </div>
  );
}
