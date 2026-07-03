import { useEffect, useState } from "react";
import { Link } from "react-router-dom";
import { api, type DashboardDto } from "../lib/api";

/**
 * Alerta visual global: exibe uma faixa no topo quando há controlador OFFLINE ou
 * sincronização pendente/falha. Atualiza sozinho a cada 30s. Some quando tudo está ok.
 */
export function StatusBanner() {
  const [dashboard, setDashboard] = useState<DashboardDto | null>(null);

  useEffect(() => {
    let active = true;
    const load = () =>
      api
        .getControllerStatus()
        .then((d) => active && setDashboard(d))
        .catch(() => active && setDashboard(null));
    load();
    const timer = setInterval(load, 30000);
    return () => {
      active = false;
      clearInterval(timer);
    };
  }, []);

  if (!dashboard) return null;

  const offline = dashboard.offline;
  const pendingSync = dashboard.controllers.reduce((sum, c) => sum + c.pendingSync, 0);
  const activeAlarms = dashboard.controllers.reduce((sum, c) => sum + c.activeAlarms, 0);

  if (offline === 0 && pendingSync === 0 && activeAlarms === 0) return null;

  const parts: string[] = [];
  if (offline > 0) parts.push(`${offline} controlador(es) offline`);
  if (pendingSync > 0) parts.push(`${pendingSync} sincronização(ões) pendente(s)`);
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
