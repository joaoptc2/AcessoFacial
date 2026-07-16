import { useEffect, useRef, useState } from "react";
import { api, ApiError, type DevLogEntry } from "../lib/api";

const POLL_INTERVAL_MS = 3000;
const MAX_LOCAL_ENTRIES = 2000; // espelha a capacidade do buffer do servidor

const LEVEL_STYLE: Record<string, { background: string; color: string }> = {
  Critical: { background: "#7f1d1d", color: "#fff" },
  Error: { background: "#fee2e2", color: "#991b1b" },
  Warning: { background: "#fef3c7", color: "#92400e" },
  Information: { background: "#e0e7ff", color: "#3730a3" },
  Debug: { background: "#f1f5f9", color: "#475569" },
  Trace: { background: "#f1f5f9", color: "#94a3b8" },
};

/**
 * Tela "Logs (Dev)": exibe os logs importantes capturados em memória enquanto o MODO DE
 * DESENVOLVIMENTO está ativo (toggle persistido no servidor). Busca incremental por id
 * (só o que ainda não temos) a cada 3s. Só Admin — a rota da API exige o papel.
 */
export function DevLogsPage() {
  const [enabled, setEnabled] = useState<boolean | null>(null);
  const [entries, setEntries] = useState<DevLogEntry[]>([]);
  const [levelFilter, setLevelFilter] = useState("");
  const [textFilter, setTextFilter] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  // Refs para o loop de polling não recriar o interval a cada novo lote.
  const lastIdRef = useRef(0);
  const loadingRef = useRef(false);

  async function fetchLogs() {
    if (loadingRef.current) return;
    loadingRef.current = true;
    try {
      const data = await api.getDevLogs({ sinceId: lastIdRef.current });
      setEnabled(data.enabled);
      if (data.entries.length > 0) {
        lastIdRef.current = data.entries[data.entries.length - 1].id;
        setEntries((prev) => {
          const merged = [...prev, ...data.entries];
          return merged.length > MAX_LOCAL_ENTRIES ? merged.slice(merged.length - MAX_LOCAL_ENTRIES) : merged;
        });
      }
      setError(null);
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Falha ao carregar os logs.");
    } finally {
      loadingRef.current = false;
    }
  }

  useEffect(() => {
    fetchLogs();
    const timer = setInterval(fetchLogs, POLL_INTERVAL_MS);
    return () => clearInterval(timer);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  async function toggleMode() {
    if (enabled === null) return;
    setBusy(true);
    try {
      const result = await api.setDevMode(!enabled);
      setEnabled(result.enabled);
      setError(null);
      await fetchLogs(); // a própria transição gera uma linha de log
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Falha ao alterar o modo de desenvolvimento.");
    } finally {
      setBusy(false);
    }
  }

  async function clearLogs() {
    if (!window.confirm("Limpar todos os logs capturados? (não afeta o log normal do serviço)")) return;
    setBusy(true);
    try {
      await api.clearDevLogs();
      setEntries([]);
      // NÃO zera lastIdRef: os ids do servidor continuam crescendo após o clear,
      // então a busca incremental segue correta.
      setError(null);
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Falha ao limpar os logs.");
    } finally {
      setBusy(false);
    }
  }

  const filtered = entries.filter((e) => {
    if (levelFilter && e.level !== levelFilter) return false;
    if (textFilter) {
      const t = textFilter.toLowerCase();
      if (!e.message.toLowerCase().includes(t) && !e.category.toLowerCase().includes(t)) return false;
    }
    return true;
  });

  return (
    <div>
      <h2>Logs de desenvolvimento</h2>
      <p className="text-muted">
        Com o modo de desenvolvimento ativo, os logs importantes do servidor (sincronização,
        monitoramento, health-check, comandos aos controladores, erros) ficam visíveis aqui —
        últimas 2000 linhas, em memória. Desative em operação normal: o modo é para diagnóstico.
      </p>

      {error && <div className="alert alert-danger">{error}</div>}

      <div className="card" style={{ marginBottom: "1rem", display: "flex", gap: "0.75rem", alignItems: "center", flexWrap: "wrap" }}>
        <span>
          Modo de desenvolvimento:{" "}
          <strong style={{ color: enabled ? "var(--success, #15803d)" : "var(--danger, #b91c1c)" }}>
            {enabled === null ? "…" : enabled ? "ATIVO" : "desativado"}
          </strong>
        </span>
        <button
          className={enabled ? "btn btn-danger-outline" : "btn btn-primary"}
          disabled={busy || enabled === null}
          onClick={toggleMode}
        >
          {enabled ? "Desativar modo de desenvolvimento" : "Ativar modo de desenvolvimento"}
        </button>
        <button className="btn btn-outline" disabled={busy} onClick={clearLogs}>
          Limpar logs
        </button>
        <span className="text-muted" style={{ marginLeft: "auto" }}>
          {filtered.length} linha(s){levelFilter || textFilter ? ` (de ${entries.length})` : ""} · atualiza a cada 3s
        </span>
      </div>

      <div className="card form-row" style={{ marginBottom: "1rem" }}>
        <div className="form-field">
          <label>Nível</label>
          <select value={levelFilter} onChange={(e) => setLevelFilter(e.target.value)}>
            <option value="">(todos)</option>
            <option value="Critical">Critical</option>
            <option value="Error">Error</option>
            <option value="Warning">Warning</option>
            <option value="Information">Information</option>
          </select>
        </div>
        <div className="form-field" style={{ flex: 1 }}>
          <label>Busca (mensagem/categoria)</label>
          <input value={textFilter} onChange={(e) => setTextFilter(e.target.value)} placeholder="ex.: sincronizar, BeginWatch, timeout…" />
        </div>
      </div>

      {entries.length === 0 ? (
        <div className="card text-muted">
          {enabled
            ? "Nenhum log capturado ainda — os logs aparecem aqui conforme o servidor trabalha."
            : "Nenhum log capturado. Ative o modo de desenvolvimento para começar a registrar."}
        </div>
      ) : (
        <table>
          <thead>
            <tr>
              <th style={{ whiteSpace: "nowrap" }}>Horário</th>
              <th>Nível</th>
              <th>Origem</th>
              <th>Mensagem</th>
            </tr>
          </thead>
          <tbody>
            {[...filtered].reverse().map((e) => {
              const style = LEVEL_STYLE[e.level] ?? LEVEL_STYLE.Debug;
              const shortCategory = e.category.split(".").pop() ?? e.category;
              return (
                <tr key={e.id}>
                  <td style={{ whiteSpace: "nowrap" }}>{new Date(e.timestampUtc).toLocaleTimeString()}</td>
                  <td>
                    <span style={{ ...style, padding: "0.1rem 0.45rem", borderRadius: "0.35rem", fontSize: "0.78rem", fontWeight: 600 }}>
                      {e.level}
                    </span>
                  </td>
                  <td title={e.category} className="text-muted" style={{ whiteSpace: "nowrap" }}>
                    {shortCategory}
                  </td>
                  <td style={{ fontFamily: "ui-monospace, monospace", fontSize: "0.85rem", wordBreak: "break-word" }}>
                    {e.message}
                    {e.exception && (
                      <details style={{ marginTop: "0.25rem" }}>
                        <summary className="text-muted" style={{ cursor: "pointer" }}>exceção</summary>
                        <pre style={{ whiteSpace: "pre-wrap", fontSize: "0.75rem", margin: "0.25rem 0 0" }}>{e.exception}</pre>
                      </details>
                    )}
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
      )}
    </div>
  );
}
