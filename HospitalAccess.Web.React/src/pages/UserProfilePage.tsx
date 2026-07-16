import { useCallback, useEffect, useState } from "react";
import { Link, useNavigate, useParams } from "react-router-dom";
import {
  api,
  ApiError,
  type ControllerDto,
  type UserAccessReport,
  type UserAuditLogEntry,
  type UserDetailDto,
  type UserGroupDto,
} from "../lib/api";
import { UserForm } from "../components/UserForm";
import { useAuth } from "../lib/AuthContext";

function fmt(iso: string | null | undefined) {
  return iso ? new Date(iso).toLocaleString() : "—";
}

function directionLabel(direction: number | null) {
  if (direction === 1) return "Entrada";
  if (direction === 2) return "Saída";
  return "—";
}

/** Perfil completo do usuário: dados, foto, relatório de portas acessadas e histórico administrativo. */
export function UserProfilePage() {
  const { id = "" } = useParams();
  const navigate = useNavigate();
  const { role } = useAuth();
  const canEdit = role === "Admin" || role === "Operator";

  const [user, setUser] = useState<UserDetailDto | null>(null);
  const [photoUrl, setPhotoUrl] = useState<string | null>(null);
  const [photoVer, setPhotoVer] = useState(0);
  const [report, setReport] = useState<UserAccessReport | null>(null);
  const [history, setHistory] = useState<UserAuditLogEntry[]>([]);
  const [groups, setGroups] = useState<UserGroupDto[]>([]);
  const [controllers, setControllers] = useState<ControllerDto[]>([]);
  const [editing, setEditing] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [range, setRange] = useState<{ from: string; to: string }>({ from: "", to: "" });

  // Relatório de portas: recebe o intervalo por argumento (não recarrega ao digitar no filtro).
  const loadReport = useCallback(
    async (from: string, to: string) => {
      const params: { from?: string; to?: string; take?: number } = { take: 100 };
      if (from) params.from = new Date(from).toISOString();
      if (to) params.to = new Date(to).toISOString();
      setReport(await api.getUserAccessLog(id, params));
    },
    [id],
  );

  const load = useCallback(async () => {
    setError(null);
    try {
      const [detail, hist] = await Promise.all([api.getUser(id), api.getUserAuditLog(id)]);
      setUser(detail);
      setHistory(hist);
      // Grupos/controladores só para o formulário de edição (endpoints restritos a Admin/Operator);
      // Recepção (só leitura) não os carrega, para não quebrar o perfil com 403.
      if (canEdit) {
        const [grp, ctrls] = await Promise.all([api.getUserGroups(), api.getControllers()]);
        setGroups(grp);
        setControllers(ctrls);
      }
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Falha ao carregar o perfil.");
    } finally {
      setLoading(false);
    }
  }, [id, canEdit]);

  useEffect(() => {
    load();
  }, [load]);

  // Carrega o relatório ao montar (sem filtro). O filtro de período só recarrega ao clicar "Aplicar".
  useEffect(() => {
    loadReport("", "");
  }, [loadReport]);

  // Foto (buscada com autenticação). O cache-buster photoVer só entra APÓS uma edição (única
  // ação que pode trocar a foto) — a visita normal usa a URL limpa e aproveita o Cache-Control
  // de 5 min do endpoint em vez de re-baixar o blob a cada ação no perfil.
  useEffect(() => {
    let active = true;
    let url: string | null = null;
    if (!user?.hasFacePhoto) {
      setPhotoUrl(null);
      return;
    }
    (async () => {
      try {
        const blob = await api.getUserPhoto(id, photoVer > 0 ? photoVer : undefined);
        if (!active) return;
        url = URL.createObjectURL(blob);
        setPhotoUrl(url);
      } catch {
        /* sem foto */
      }
    })();
    return () => {
      active = false;
      if (url) URL.revokeObjectURL(url);
    };
  }, [id, user?.hasFacePhoto, photoVer]);

  async function act(action: () => Promise<void>, failMsg: string) {
    setError(null);
    try {
      await action();
      await load();
    } catch (err) {
      setError(err instanceof ApiError ? err.message : failMsg);
    }
  }

  async function handleResync() {
    setError(null);
    setNotice(null);
    try {
      await api.resyncUser(id);
      setNotice("Sincronização reenfileirada (roda em segundo plano).");
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Falha ao reenfileirar sincronização.");
    }
  }

  async function handleDelete() {
    setError(null);
    try {
      await api.deleteUser(id);
      navigate("/users");
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Falha ao excluir.");
    }
  }

  if (loading) return <p className="text-muted">Carregando perfil...</p>;
  if (!user) return <p className="alert alert-danger">{error ?? "Usuário não encontrado."}</p>;

  return (
    <div>
      <div className="page-header">
        <div>
          <Link to="/users" className="text-muted" style={{ textDecoration: "none", fontSize: "0.85rem" }}>
            ← Usuários
          </Link>
          <h2 style={{ margin: "0.15rem 0 0" }}>
            {user.name}{" "}
            <span className={user.revokedAtUtc ? "pill" : "pill pill-success"} style={{ verticalAlign: "middle" }}>
              {user.revokedAtUtc ? "Revogado" : "Ativo"}
            </span>
          </h2>
          <span className="text-muted">
            Código {user.userCode}
            {user.jobTitle ? ` · ${user.jobTitle}` : ""}
            {user.groupName ? ` · ${user.groupName}` : ""}
          </span>
        </div>
        {canEdit && !editing && (
          <div className="btn-group">
            <button className="btn btn-primary" onClick={() => setEditing(true)}>
              Editar
            </button>
            <button className="btn btn-outline" onClick={handleResync}>
              Sincronizar
            </button>
            {user.revokedAtUtc === null ? (
              <button className="btn btn-outline" onClick={() => act(() => api.revokeUser(id), "Falha ao revogar.")}>
                Revogar
              </button>
            ) : (
              <button className="btn btn-outline" onClick={() => act(() => api.reactivateUser(id), "Falha ao reativar.")}>
                Reativar
              </button>
            )}
            <button className="btn btn-danger-outline" onClick={handleDelete}>
              Excluir
            </button>
          </div>
        )}
      </div>

      {error && <div className="alert alert-danger">{error}</div>}
      {notice && <div className="alert alert-success">{notice}</div>}

      {editing && canEdit && (
        <div className="card" style={{ marginBottom: "1.25rem" }}>
          <h3 style={{ marginTop: 0, fontSize: "1.05rem" }}>Editar cadastro</h3>
          <UserForm
            mode="edit"
            userId={id}
            groups={groups}
            controllers={controllers}
            onSaved={() => {
              setEditing(false);
              setPhotoVer((v) => v + 1); // a edição pode ter trocado a foto — força re-download
              load();
            }}
            onCancel={() => setEditing(false)}
          />
        </div>
      )}

      <div className="profile-grid">
        <div className="profile-col">
          <div className="card">
            <h4 className="section-title">Foto</h4>
            {photoUrl ? (
              <img src={photoUrl} alt={`Foto de ${user.name}`} className="profile-photo" />
            ) : (
              <div className="profile-photo profile-photo-empty">Sem foto</div>
            )}
          </div>

          <div className="card">
            <h4 className="section-title">Dados</h4>
            <dl className="detail-list">
              <dt>Documento</dt>
              <dd>{user.document || "—"}</dd>
              <dt>Matrícula</dt>
              <dd>{user.employeeId || "—"}</dd>
              <dt>Cargo</dt>
              <dd>{user.jobTitle || "—"}</dd>
              <dt>Telefone</dt>
              <dd>{user.phone || "—"}</dd>
              <dt>E-mail</dt>
              <dd>{user.email || "—"}</dd>
              <dt>Grupo</dt>
              <dd>{user.groupName || "—"}</dd>
              <dt>Grupo de horário</dt>
              <dd>{user.timeGroup}</dd>
              <dt>Cartão</dt>
              <dd>{user.cardNumber ?? "—"}</dd>
              <dt>Portas</dt>
              <dd>
                {user.controllers.length === 0
                  ? "—"
                  : user.controllers.map((c) => (
                      <span key={c.controllerId} className="pill" style={{ margin: "0 0.25rem 0.25rem 0" }}>
                        {c.controllerName}
                        {c.grantedByGroup ? " (grupo)" : ""}
                      </span>
                    ))}
              </dd>
              <dt>Cadastrado</dt>
              <dd>
                {fmt(user.createdAtUtc)}
                {user.createdByUsername ? ` por ${user.createdByUsername}` : ""}
              </dd>
              {user.revokedAtUtc && (
                <>
                  <dt>Revogado</dt>
                  <dd>
                    {fmt(user.revokedAtUtc)}
                    {user.revokedByUsername ? ` por ${user.revokedByUsername}` : ""}
                  </dd>
                </>
              )}
              {user.notes && (
                <>
                  <dt>Observações</dt>
                  <dd style={{ whiteSpace: "pre-wrap" }}>{user.notes}</dd>
                </>
              )}
            </dl>
          </div>
        </div>

        <div className="profile-col">
          <div className="card">
            <div className="section-head">
              <h4 className="section-title" style={{ margin: 0 }}>
                Portas acessadas
              </h4>
              <div className="range-filter">
                <input type="datetime-local" value={range.from} onChange={(e) => setRange({ ...range, from: e.target.value })} />
                <span className="text-muted">até</span>
                <input type="datetime-local" value={range.to} onChange={(e) => setRange({ ...range, to: e.target.value })} />
                <button className="btn btn-outline btn-sm" onClick={() => loadReport(range.from, range.to)}>
                  Aplicar
                </button>
              </div>
            </div>

            {report && (
              <>
                <div className="stat-row">
                  <div className="stat-tile">
                    <span className="stat-value">{report.total}</span>
                    <span className="stat-label">Acessos</span>
                  </div>
                  <div className="stat-tile">
                    <span className="stat-value" style={{ color: "var(--success)" }}>
                      {report.granted}
                    </span>
                    <span className="stat-label">Concedidos</span>
                  </div>
                  <div className="stat-tile">
                    <span className="stat-value" style={{ color: "var(--danger)" }}>
                      {report.denied}
                    </span>
                    <span className="stat-label">Negados</span>
                  </div>
                  <div className="stat-tile">
                    <span className="stat-value">{report.distinctDoors}</span>
                    <span className="stat-label">Portas</span>
                  </div>
                  <div className="stat-tile">
                    <span className="stat-value" style={{ fontSize: "0.9rem" }}>
                      {report.lastAccessUtc ? fmt(report.lastAccessUtc) : "—"}
                    </span>
                    <span className="stat-label">Último acesso</span>
                  </div>
                </div>

                {report.doors.length === 0 ? (
                  <p className="text-muted" style={{ marginBottom: 0 }}>
                    Nenhum acesso registrado no período.
                  </p>
                ) : (
                  <>
                    <h5 className="section-subtitle">Resumo por porta</h5>
                    <div className="table-wrap">
                      <table>
                        <thead>
                          <tr>
                            <th>Porta</th>
                            <th>Acessos</th>
                            <th>Concedidos</th>
                            <th>Última vez</th>
                          </tr>
                        </thead>
                        <tbody>
                          {report.doors.map((d) => (
                            <tr key={d.controllerId}>
                              <td>{d.controllerName}</td>
                              <td>{d.total}</td>
                              <td>{d.granted}</td>
                              <td>{fmt(d.lastUtc)}</td>
                            </tr>
                          ))}
                        </tbody>
                      </table>
                    </div>

                    <h5 className="section-subtitle">Eventos recentes</h5>
                    <div className="table-wrap">
                      <table>
                        <thead>
                          <tr>
                            <th>Data/hora</th>
                            <th>Porta</th>
                            <th>Método</th>
                            <th>Sentido</th>
                            <th>Resultado</th>
                          </tr>
                        </thead>
                        <tbody>
                          {report.items.map((ev, i) => (
                            <tr key={i}>
                              <td>{fmt(ev.timestampUtc)}</td>
                              <td>{ev.controllerName}</td>
                              <td>{ev.method}</td>
                              <td>{directionLabel(ev.direction)}</td>
                              <td>
                                <span className={ev.granted ? "pill pill-success" : "pill pill-danger"}>
                                  {ev.granted ? "Concedido" : "Negado"}
                                </span>
                              </td>
                            </tr>
                          ))}
                        </tbody>
                      </table>
                    </div>
                  </>
                )}
              </>
            )}
          </div>

          <div className="card">
            <h4 className="section-title">Histórico administrativo</h4>
            {history.length === 0 ? (
              <p className="text-muted" style={{ margin: 0 }}>
                Sem registros.
              </p>
            ) : (
              <ul style={{ margin: 0, paddingLeft: "1.1rem" }}>
                {history.map((entry, i) => (
                  <li key={i}>
                    {fmt(entry.timestampUtc)} — <strong>{entry.action}</strong> ({entry.performedByUsername ?? "sistema"})
                    {entry.details ? ` — ${entry.details}` : ""}
                  </li>
                ))}
              </ul>
            )}
          </div>
        </div>
      </div>
    </div>
  );
}
