import { useState } from "react";
import { useAuth } from "../lib/AuthContext";
import { api, ApiError, type EmergencyResultDto } from "../lib/api";
import { refreshControllerStatus, useControllerStatus } from "../lib/controllerStatusStore";

export function HomePage() {
  const { username, role } = useAuth();
  const isAdmin = role === "Admin";

  // Polling compartilhado com o StatusBanner (uma única requisição por ciclo de 30s).
  const { dashboard, error: statusError } = useControllerStatus();
  const [error, setError] = useState<string | null>(null);
  const [emergencyBusy, setEmergencyBusy] = useState(false);
  const [emergencyResult, setEmergencyResult] = useState<EmergencyResultDto | null>(null);

  async function runEmergency(kind: "activate" | "deactivate" | "fire" | "clear") {
    const messages: Record<typeof kind, string> = {
      activate: "ATIVAR EMERGÊNCIA: abrir TODAS as portas e disparar o alarme de incêndio em todos os controladores. Confirmar?",
      deactivate: "Encerrar a emergência: fechar as portas e silenciar os alarmes. Confirmar?",
      fire: "Disparar o alarme de INCÊNDIO em TODOS os controladores (sem mexer nas portas). Confirmar?",
      clear: "Silenciar os alarmes em TODOS os controladores. Confirmar?",
    };
    if (!window.confirm(messages[kind])) return;

    setEmergencyBusy(true);
    setEmergencyResult(null);
    setError(null); // um erro de comando anterior não pode ficar preso na tela
    try {
      const result =
        kind === "activate"
          ? await api.activateEmergency()
          : kind === "deactivate"
            ? await api.deactivateEmergency()
            : kind === "fire"
              ? await api.fireAlarmAll()
              : await api.clearAlarmsAll();
      setEmergencyResult(result);
      await refreshControllerStatus();
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Falha no comando de emergência.");
    } finally {
      setEmergencyBusy(false);
    }
  }

  return (
    <div>
      <h2>Painel de Controle de Acesso Hospitalar</h2>
      <p className="text-muted">
        Bem-vindo(a), {username} ({role}).
      </p>

      {(error ?? statusError) && <div className="alert alert-danger">{error ?? statusError}</div>}

      {dashboard && (
        <div className="card" style={{ marginBottom: "1.25rem" }}>
          <div style={{ display: "flex", gap: "1.5rem", flexWrap: "wrap" }}>
            <Stat label="Controladores" value={dashboard.total} />
            <Stat label="Online" value={dashboard.online} tone="success" />
            <Stat label="Offline" value={dashboard.offline} tone={dashboard.offline > 0 ? "danger" : undefined} />
          </div>
        </div>
      )}

      {isAdmin && (
        <div className="card" style={{ marginBottom: "1.25rem", borderColor: "#fecaca" }}>
          <h3 style={{ marginTop: 0 }}>Emergência / Evacuação</h3>
          <p className="text-muted" style={{ marginTop: 0 }}>
            Abre todas as portas e dispara o alarme de incêndio em todos os controladores. Ação auditada.
          </p>
          <div className="btn-group" style={{ flexWrap: "wrap" }}>
            <button className="btn btn-danger" disabled={emergencyBusy} onClick={() => runEmergency("activate")}>
              Ativar emergência (abrir tudo + incêndio)
            </button>
            <button className="btn btn-danger-outline" disabled={emergencyBusy} onClick={() => runEmergency("fire")}>
              Disparar incêndio (todos)
            </button>
            <button className="btn btn-outline" disabled={emergencyBusy} onClick={() => runEmergency("clear")}>
              Silenciar alarmes (todos)
            </button>
            <button className="btn btn-outline" disabled={emergencyBusy} onClick={() => runEmergency("deactivate")}>
              Encerrar emergência
            </button>
          </div>
          {emergencyResult && (
            <div
              className={emergencyResult.failed > 0 ? "alert alert-danger" : "alert alert-success"}
              style={{ marginTop: "0.75rem", marginBottom: 0 }}
            >
              <strong>{emergencyResult.action}</strong>: {emergencyResult.succeeded}/{emergencyResult.total} controlador(es) OK
              {emergencyResult.failed > 0 && ` — ${emergencyResult.failed} falha(s): ${emergencyResult.devices.filter((d) => !d.success).map((d) => d.controllerName).join(", ")}`}
            </div>
          )}
        </div>
      )}

      {dashboard && (
        <table>
          <thead>
            <tr>
              <th>Controlador</th>
              <th>IP</th>
              <th>Status</th>
              <th>Último contato</th>
              <th>Sync pendente</th>
              <th>Alarmes ativos</th>
            </tr>
          </thead>
          <tbody>
            {dashboard.controllers.map((c) => (
              <tr key={c.id}>
                <td>{c.name}</td>
                <td>{c.ipAddress}</td>
                <td>
                  <span className={c.online ? "pill pill-success" : "pill pill-danger"}>
                    {c.online ? "Online" : "Offline"}
                  </span>
                </td>
                <td>{c.lastSeenUtc ? new Date(c.lastSeenUtc).toLocaleString() : "nunca"}</td>
                <td>
                  {c.pendingSync > 0 ? (
                    <span
                      className="pill pill-warning"
                      title={
                        (c.awaitingManualSync ?? 0) > 0
                          ? `${c.awaitingManualSync} com erro permanente (ação manual na tela de usuários)`
                          : "aguardando sincronização automática"
                      }
                    >
                      {c.pendingSync}
                      {(c.awaitingManualSync ?? 0) > 0 ? ` (${c.awaitingManualSync} manual)` : ""}
                    </span>
                  ) : (
                    "0"
                  )}
                </td>
                <td>{c.activeAlarms > 0 ? <span className="pill pill-danger">{c.activeAlarms}</span> : "0"}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </div>
  );
}

function Stat({ label, value, tone }: { label: string; value: number; tone?: "success" | "danger" }) {
  const color = tone === "success" ? "var(--success)" : tone === "danger" ? "var(--danger)" : "inherit";
  return (
    <div>
      <div style={{ fontSize: "1.75rem", fontWeight: 700, color }}>{value}</div>
      <div className="text-muted">{label}</div>
    </div>
  );
}
