import { Link } from "react-router-dom";
import type { PersonnelAuditAllResult } from "../../lib/api";

interface Props {
  result: PersonnelAuditAllResult | null;
  error: string | null;
  notice: string | null;
  /** Trava os botões enquanto um reparo está em voo. */
  busy: boolean;
  onResyncUser: (controllerId: string, userId: string, name: string) => void;
  onRepairController: (controllerId: string, controllerName: string, missingCount: number) => void;
}

/**
 * Resultado da auditoria de usuários em todos os controladores: quem está em dia, quem divergiu
 * e quem não respondeu — com o reparo (reenvio) direto de cada faltante.
 *
 * Extraído da ControllersPage, que passava de 800 linhas. É puramente apresentacional: recebe o
 * resultado já pronto e devolve as ações por callback, sem falar com a API.
 */
export function PersonnelAuditPanel({ result, error, notice, busy, onResyncUser, onRepairController }: Props) {
  if (!result && !error) return null;

  return (
      <div className="card" style={{ marginTop: "1rem" }}>
        <div style={{ display: "flex", justifyContent: "space-between", alignItems: "baseline", gap: "0.75rem", flexWrap: "wrap" }}>
          <h3 style={{ margin: 0, fontSize: "1.05rem" }}>Auditoria de usuários — todos os controladores</h3>
          {result && (
            <span className="text-muted" style={{ fontSize: "0.85rem" }}>
              {new Date(result.generatedAtUtc).toLocaleString()}
            </span>
          )}
        </div>
        {error && <div className="alert alert-danger" style={{ marginTop: "0.75rem" }}>{error}</div>}
        {notice && <div className="alert alert-success" style={{ marginTop: "0.75rem" }}>{notice}</div>}
        {result && (() => {
          const unreachable = result.results.filter((r) => r.error !== null);
          const diverging = result.results.filter(
            (r) => r.error === null && ((r.missingOnDevice?.length ?? 0) > 0 || (r.extraOnDevice?.length ?? 0) > 0),
          );
          const okCount = result.results.length - unreachable.length - diverging.length;
          return (
            <>
              <p style={{ margin: "0.75rem 0" }}>
                {result.results.length} controlador(es): <span className="pill pill-success">{okCount} em dia</span>{" "}
                {diverging.length > 0 && <span className="pill pill-warning">{diverging.length} com divergência</span>}{" "}
                {unreachable.length > 0 && <span className="pill pill-danger">{unreachable.length} inalcançáveis</span>}
              </p>
              {unreachable.map((r) => (
                <div key={r.controllerId} className="alert alert-danger" style={{ marginBottom: "0.5rem" }}>
                  <strong>{r.controllerName}</strong>: {r.error}
                </div>
              ))}
              {diverging.map((r) => (
                <div key={r.controllerId} style={{ borderTop: "1px solid var(--border, #e2e8f0)", paddingTop: "0.75rem", marginTop: "0.75rem" }}>
                  <p style={{ margin: "0 0 0.5rem" }}>
                    <Link to={`/controllers/${r.controllerId}`} className="link-strong">
                      {r.controllerName}
                    </Link>{" "}
                    — no aparelho: <strong>{r.deviceCount}</strong> · esperado: <strong>{r.expectedCount}</strong>
                  </p>
                  {(r.missingOnDevice?.length ?? 0) > 0 && (
                    <div style={{ marginBottom: "0.5rem" }}>
                      <p className="text-muted" style={{ margin: "0 0 0.3rem", fontSize: "0.85rem", textTransform: "uppercase" }}>
                        Faltando no dispositivo
                      </p>
                      <ul style={{ listStyle: "none", paddingLeft: 0, margin: 0 }}>
                        {r.missingOnDevice!.map((u) => (
                          <li key={u.userId} style={{ display: "flex", alignItems: "center", gap: "0.5rem", marginBottom: "0.3rem" }}>
                            <span>
                              {u.name} <span className="text-muted">#{u.userCode}</span>
                              {u.type === "Visitor" && <span className="pill" style={{ marginLeft: "0.4rem" }}>Visitante</span>}
                            </span>
                            <button
                              className="btn btn-outline btn-sm"
                              disabled={busy}
                              onClick={() => onResyncUser(r.controllerId, u.userId, u.name)}
                            >
                              Resincronizar
                            </button>
                          </li>
                        ))}
                      </ul>
                      {r.missingOnDevice!.length > 1 && (
                        <button
                          className="btn btn-primary btn-sm"
                          style={{ marginTop: "0.3rem" }}
                          disabled={busy}
                          onClick={() => onRepairController(r.controllerId, r.controllerName, r.missingOnDevice!.length)}
                        >
                          Resincronizar todos os faltantes
                        </button>
                      )}
                    </div>
                  )}
                  {(r.extraOnDevice?.length ?? 0) > 0 && (
                    <p className="text-muted" style={{ margin: 0, fontSize: "0.9rem" }}>
                      Extra no dispositivo (não esperado): {r.extraOnDevice!.join(", ")} — exclua pela página de
                      detalhes (aba Auditoria).
                    </p>
                  )}
                </div>
              ))}
              {diverging.length === 0 && unreachable.length === 0 && (
                <p className="text-muted" style={{ margin: 0 }}>
                  Nenhuma divergência: todos os aparelhos têm exatamente os usuários esperados.
                </p>
              )}
            </>
          );
        })()}
      </div>
  );
}
