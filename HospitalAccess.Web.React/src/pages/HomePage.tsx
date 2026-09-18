import { useState } from "react";
import { useAuth } from "../lib/AuthContext";
import { useFeedback } from "../lib/feedback";
import { api, ApiError, type EmergencyResultDto } from "../lib/api";
import { refreshControllerStatus, useControllerStatus } from "../lib/controllerStatusStore";
import { Icon, type IconName } from "../components/Icon";

type Tom = "neutro" | "success" | "warning" | "danger";

/** Cartão de indicador: bloco de ícone + número + rótulo. */
function StatCard({ icon, value, label, tone = "neutro" }: { icon: IconName; value: number; label: string; tone?: Tom }) {
  return (
    <div className={`stat-card${tone === "neutro" ? "" : ` is-${tone}`}`}>
      <span className="stat-icon">
        <Icon name={icon} size={21} />
      </span>
      <div className="stat-body">
        <div className="stat-value">{value}</div>
        <div className="stat-label">{label}</div>
      </div>
    </div>
  );
}

/** Os quatro comandos de emergência, com o texto da confirmação junto. */
const EMERGENCIA = {
  activate: {
    rotulo: "Ativar emergência",
    title: "Ativar a emergência em todos os controladores?",
    text: "Abre TODAS as portas do hospital e dispara o alarme de incêndio. A ação é registrada em auditoria com o seu usuário.",
    confirmLabel: "Ativar emergência",
    danger: true,
  },
  fire: {
    rotulo: "Disparar incêndio",
    title: "Disparar o alarme de incêndio em todos os controladores?",
    text: "As portas não são abertas — só o alarme sonoro é acionado.",
    confirmLabel: "Disparar alarme",
    danger: true,
  },
  clear: {
    rotulo: "Silenciar alarmes",
    title: "Silenciar os alarmes de todos os controladores?",
    text: "Encerra o som. Não fecha portas que estejam abertas pela emergência.",
    confirmLabel: "Silenciar",
    danger: false,
  },
  deactivate: {
    rotulo: "Encerrar emergência",
    title: "Encerrar a emergência?",
    text: "Fecha as portas e silencia os alarmes em todos os controladores.",
    confirmLabel: "Encerrar",
    danger: false,
  },
} as const;

type ComandoEmergencia = keyof typeof EMERGENCIA;

export function HomePage() {
  const { username, role } = useAuth();
  const { confirm, toastSuccess, toastError } = useFeedback();
  const isAdmin = role === "Admin";

  // Polling compartilhado com o StatusBanner (uma única requisição por ciclo de 30s).
  const { dashboard, error: statusError } = useControllerStatus();
  const [emAndamento, setEmAndamento] = useState<ComandoEmergencia | null>(null);
  const [falhas, setFalhas] = useState<EmergencyResultDto | null>(null);

  async function runEmergency(kind: ComandoEmergencia) {
    const cfg = EMERGENCIA[kind];
    if (!(await confirm(cfg))) return;

    setEmAndamento(kind);
    setFalhas(null);
    try {
      const result =
        kind === "activate"
          ? await api.activateEmergency()
          : kind === "deactivate"
            ? await api.deactivateEmergency()
            : kind === "fire"
              ? await api.fireAlarmAll()
              : await api.clearAlarmsAll();

      if (result.failed > 0) {
        setFalhas(result);
        toastError(`${cfg.rotulo}: ${result.succeeded} de ${result.total} controladores responderam.`);
      } else {
        toastSuccess(`${cfg.rotulo}: ${result.total} controlador(es) confirmaram.`);
      }
      await refreshControllerStatus();
    } catch (err) {
      toastError(err instanceof ApiError ? err.message : "Falha no comando de emergência.");
    } finally {
      setEmAndamento(null);
    }
  }

  const pendentes = dashboard?.controllers.reduce((s, c) => s + c.pendingSync, 0) ?? 0;
  const alarmes = dashboard?.controllers.reduce((s, c) => s + c.activeAlarms, 0) ?? 0;

  return (
    <div>
      <div className="page-header">
        <div>
          <h2 style={{ marginBottom: "0.15rem" }}>Olá, {username}</h2>
          <p className="text-muted" style={{ margin: 0 }}>
            Situação do parque de controladores agora.
            {role && <span className="badge-role" style={{ marginLeft: "0.5rem" }}>{role}</span>}
          </p>
        </div>
      </div>

      {statusError && <div className="alert alert-danger">{statusError}</div>}

      {dashboard && (
        <div className="stat-grid">
          <StatCard icon="device" value={dashboard.total} label="Controladores" />
          <StatCard icon="check" value={dashboard.online} label="Online" tone="success" />
          <StatCard icon="alert" value={dashboard.offline} label="Offline" tone={dashboard.offline > 0 ? "danger" : "neutro"} />
          <StatCard icon="sync" value={pendentes} label="Sync pendente" tone={pendentes > 0 ? "warning" : "neutro"} />
          <StatCard icon="alarm" value={alarmes} label="Alarmes ativos" tone={alarmes > 0 ? "danger" : "neutro"} />
        </div>
      )}

      {isAdmin && (
        <div className="panel">
          <div className="panel-head">
            <Icon name="alert" size={18} />
            <h3>Emergência / Evacuação</h3>
          </div>
          <div className="panel-body">
            <p className="text-muted" style={{ marginTop: 0 }}>
              Abre todas as portas e dispara o alarme de incêndio em todos os controladores. Toda ação daqui é
              registrada em auditoria com o seu usuário.
            </p>
            <div className="btn-group" style={{ flexWrap: "wrap" }}>
              {(Object.keys(EMERGENCIA) as ComandoEmergencia[]).map((kind) => (
                <button
                  key={kind}
                  className={`btn ${kind === "activate" ? "btn-danger" : kind === "fire" ? "btn-danger-outline" : "btn-outline"}${
                    emAndamento === kind ? " is-busy" : ""
                  }`}
                  disabled={emAndamento !== null}
                  onClick={() => runEmergency(kind)}
                >
                  {EMERGENCIA[kind].rotulo}
                </button>
              ))}
            </div>

            {falhas && (
              <div className="alert alert-danger" style={{ marginTop: "0.9rem", marginBottom: 0 }}>
                <strong>{falhas.action}</strong> — {falhas.failed} controlador(es) não responderam:{" "}
                {falhas.devices.filter((d) => !d.success).map((d) => d.controllerName).join(", ")}
              </div>
            )}
          </div>
        </div>
      )}

      {dashboard && (
        <div className="panel">
          <div className="panel-head">
            <Icon name="device" size={18} />
            <h3>Controladores</h3>
            <span className="panel-count">{dashboard.controllers.length}</span>
          </div>
          <div className="panel-body flush">
            {dashboard.controllers.length === 0 ? (
              <div className="panel-empty">Nenhum controlador cadastrado ainda.</div>
            ) : (
              <div className="table-wrap">
                <table>
                  <thead>
                    <tr>
                      <th>Controlador</th>
                      <th>IP</th>
                      <th>Status</th>
                      <th>Último contato</th>
                      <th>Sync pendente</th>
                      <th>Alarmes</th>
                    </tr>
                  </thead>
                  <tbody>
                    {dashboard.controllers.map((c) => (
                      <tr key={c.id}>
                        <td>{c.name}</td>
                        <td>
                          <code>{c.ipAddress}</code>
                        </td>
                        <td>
                          <span className={c.online ? "pill pill-success" : "pill pill-danger"}>
                            {c.online ? "Online" : "Offline"}
                          </span>
                          {c.circuitOpenUntilUtc && (
                            <span
                              className="pill pill-warning"
                              style={{ marginLeft: "0.35rem" }}
                              title="O aparelho responde à rede mas não ao protocolo; os comandos estão em espera para protegê-lo. Use 'Testar conexão' na tela do controlador para sondar agora."
                            >
                              protocolo em espera até {new Date(c.circuitOpenUntilUtc).toLocaleTimeString()}
                            </span>
                          )}
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
                            <span className="text-muted">0</span>
                          )}
                        </td>
                        <td>
                          {c.activeAlarms > 0 ? (
                            <span className="pill pill-danger">{c.activeAlarms}</span>
                          ) : (
                            <span className="text-muted">0</span>
                          )}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </div>
        </div>
      )}
    </div>
  );
}
