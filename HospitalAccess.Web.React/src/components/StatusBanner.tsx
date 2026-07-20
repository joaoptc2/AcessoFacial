import { Link } from "react-router-dom";
import { useControllerStatus } from "../lib/controllerStatusStore";

/**
 * Alerta visual global: exibe uma faixa no topo quando há controlador OFFLINE ou
 * sincronização pendente/falha. Atualiza sozinho a cada 30s (polling compartilhado
 * com a HomePage via controllerStatusStore — uma única requisição por ciclo).
 * Some quando tudo está ok.
 */
export function StatusBanner() {
  const { dashboard } = useControllerStatus();

  if (!dashboard) return null;

  const offline = dashboard.offline;
  const manualSync = dashboard.controllers.reduce((sum, c) => sum + (c.awaitingManualSync ?? 0), 0);
  // "Pendente" de verdade = o que o retry automático ainda vai tentar; quarentena vai em separado.
  const pendingSync = dashboard.controllers.reduce((sum, c) => sum + c.pendingSync, 0) - manualSync;
  const activeAlarms = dashboard.controllers.reduce((sum, c) => sum + c.activeAlarms, 0);

  if (offline === 0 && pendingSync === 0 && manualSync === 0 && activeAlarms === 0) return null;

  const parts: string[] = [];
  if (offline > 0) parts.push(`${offline} controlador(es) offline`);
  if (pendingSync > 0) parts.push(`${pendingSync} sincronização(ões) pendente(s)`);
  if (manualSync > 0) parts.push(`${manualSync} sincronização(ões) com erro permanente — resolver na tela de usuários`);
  if (activeAlarms > 0) parts.push(`${activeAlarms} alarme(s) ativo(s)`);

  const critical = offline > 0 || activeAlarms > 0;

  return (
    <div
      role="alert"
      style={{
        background: critical ? "#fef2f2" : "#fffbeb",
        borderBottom: `2px solid ${critical ? "var(--danger)" : "var(--warning)"}`,
        color: critical ? "#991b1b" : "#92400e",
        padding: "0.5rem 1rem",
        fontSize: "0.9rem",
        display: "flex",
        alignItems: "center",
        gap: "0.75rem",
      }}
    >
      <span style={{ fontWeight: 600 }}>⚠ Atenção:</span>
      <span>{parts.join(" · ")}</span>
      <Link to="/" style={{ marginLeft: "auto", color: "inherit", fontWeight: 600 }}>
        Ver painel
      </Link>
    </div>
  );
}
