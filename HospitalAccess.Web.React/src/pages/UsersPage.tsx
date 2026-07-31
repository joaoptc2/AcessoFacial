import { Fragment, useCallback, useEffect, useState } from "react";
import { Link } from "react-router-dom";
import { api, ApiError, type ControllerDto, type UserAuditLogEntry, type UserGroupDto, type UserListItemDto } from "../lib/api";
import { UserForm } from "../components/UserForm";
import { useAuth } from "../lib/AuthContext";

const PAGE_SIZE = 25;

export function UsersPage() {
  const { role } = useAuth();
  const canEdit = role === "Admin" || role === "Operator";

  const [users, setUsers] = useState<UserListItemDto[]>([]);
  const [total, setTotal] = useState(0);
  const [page, setPage] = useState(1);
  const [groups, setGroups] = useState<UserGroupDto[]>([]);
  const [controllers, setControllers] = useState<ControllerDto[]>([]);
  const [creating, setCreating] = useState(false);
  const [editingId, setEditingId] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [search, setSearch] = useState("");
  const [debouncedSearch, setDebouncedSearch] = useState("");
  const [groupFilter, setGroupFilter] = useState("");
  const [historyUserId, setHistoryUserId] = useState<string | null>(null);
  const [historyEntries, setHistoryEntries] = useState<UserAuditLogEntry[]>([]);

  // Busca/filtro rodam no SERVIDOR (lista paginada): debounce para não consultar a cada tecla.
  useEffect(() => {
    const timer = setTimeout(() => setDebouncedSearch(search.trim()), 400);
    return () => clearTimeout(timer);
  }, [search]);

  useEffect(() => {
    setPage(1);
  }, [debouncedSearch, groupFilter]);

  const load = useCallback(async () => {
    const result = await api.getUsers({
      page,
      pageSize: PAGE_SIZE,
      search: debouncedSearch || undefined,
      groupId: groupFilter || undefined,
    });
    // Página esvaziou (ex.: exclusão do último item): volta para a última página existente.
    if (result.items.length === 0 && result.total > 0 && page > 1) {
      setPage(Math.max(1, Math.ceil(result.total / PAGE_SIZE)));
      return;
    }
    setUsers(result.items);
    setTotal(result.total);
  }, [page, debouncedSearch, groupFilter]);

  useEffect(() => {
    load();
  }, [load]);

  // Grupos/controladores só para o formulário e o filtro (endpoints restritos a Admin/Operator);
  // Recepção (só leitura) não os carrega, evitando 403.
  useEffect(() => {
    if (!canEdit) return;
    (async () => {
      setGroups(await api.getUserGroups());
      setControllers(await api.getControllers());
    })();
  }, [canEdit]);

  const totalPages = Math.max(1, Math.ceil(total / PAGE_SIZE));

  function startCreate() {
    setEditingId(null);
    setHistoryUserId(null);
    setCreating(true);
  }

  function startEdit(id: string) {
    setCreating(false);
    setHistoryUserId(null);
    setEditingId((prev) => (prev === id ? null : id));
  }

  async function afterCreate() {
    setCreating(false);
    await load();
  }

  async function afterEdit() {
    setEditingId(null);
    await load();
  }

  async function withReload(action: () => Promise<void>, failMsg: string) {
    setError(null);
    try {
      await action();
      await load();
    } catch (err) {
      setError(err instanceof ApiError ? err.message : failMsg);
    }
  }

  async function toggleHistory(id: string) {
    if (historyUserId === id) {
      setHistoryUserId(null);
      return;
    }
    setEditingId(null);
    setHistoryUserId(id);
    setHistoryEntries(await api.getUserAuditLog(id));
  }

  return (
    <div>
      <div className="page-header">
        <h2>Usuários permanentes (acesso por face)</h2>
        {canEdit && (
          <div className="btn-group">
            {!creating && (
              <button className="btn btn-primary" onClick={startCreate}>
                + Novo usuário
              </button>
            )}
          </div>
        )}
      </div>

      {!canEdit && <p className="text-muted">Seu papel (Recepção) tem acesso só de leitura a usuários permanentes.</p>}

      {creating && canEdit && (
        <div className="card" style={{ marginBottom: "1.25rem" }}>
          <h3 style={{ marginTop: 0, fontSize: "1.05rem" }}>Novo usuário</h3>
          <UserForm mode="create" groups={groups} controllers={controllers} onSaved={afterCreate} onCancel={() => setCreating(false)} />
        </div>
      )}

      <div className="card form-row" style={{ marginBottom: "1rem" }}>
        <div className="form-field" style={{ flex: "1 1 260px" }}>
          <label>Buscar</label>
          <input placeholder="Nome ou código..." value={search} onChange={(e) => setSearch(e.target.value)} />
        </div>
        <div className="form-field">
          <label>Grupo</label>
          <select value={groupFilter} onChange={(e) => setGroupFilter(e.target.value)}>
            <option value="">(todos)</option>
            {groups.map((g) => (
              <option key={g.id} value={g.id}>
                {g.name}
              </option>
            ))}
          </select>
        </div>
      </div>

      {error && <div className="alert alert-danger">{error}</div>}

      <div className="table-wrap">
        <table>
          <thead>
            <tr>
              <th>Nome</th>
              <th>Código</th>
              <th>Grupo</th>
              <th>Cartão</th>
              <th>Portas</th>
              <th>Foto</th>
              <th>Status</th>
              <th>Ações</th>
            </tr>
          </thead>
          <tbody>
            {users.map((u) => (
              <Fragment key={u.id}>
                <tr className={editingId === u.id ? "row-active" : undefined} style={u.revokedAtUtc ? { background: "var(--surface-alt)" } : undefined}>
                  <td>
                    <Link to={`/users/${u.id}`} className="link-strong">
                      {u.name}
                    </Link>
                  </td>
                  <td>{u.userCode}</td>
                  <td>{u.groupName ?? "—"}</td>
                  <td>{u.cardNumber ?? "—"}</td>
                  <td>{u.controllers.length === 0 ? "—" : u.controllers.map((c) => c.controllerName).join(", ")}</td>
                  <td>{u.hasFacePhoto ? "✔" : "—"}</td>
                  <td>
                    <span className={u.revokedAtUtc ? "pill" : "pill pill-success"}>{u.revokedAtUtc ? "Revogado" : "Ativo"}</span>
                  </td>
                  <td>
                    <div className="btn-group">
                      {canEdit && (
                        <>
                          <button className={`btn btn-sm ${editingId === u.id ? "btn-primary" : "btn-outline"}`} onClick={() => startEdit(u.id)}>
                            Editar
                          </button>
                          {u.revokedAtUtc === null ? (
                            <button
                              className="btn btn-outline btn-sm"
                              style={{ borderColor: "#fde68a", color: "var(--warning)" }}
                              onClick={() => withReload(() => api.revokeUser(u.id), "Falha ao revogar.")}
                            >
                              Revogar
                            </button>
                          ) : (
                            <button
                              className="btn btn-outline btn-sm"
                              style={{ borderColor: "#bbf7d0", color: "var(--success)" }}
                              onClick={() => withReload(() => api.reactivateUser(u.id), "Falha ao reativar.")}
                            >
                              Reativar
                            </button>
                          )}
                          <button
                            className="btn btn-danger-outline btn-sm"
                            onClick={() => {
                              if (!window.confirm(`Excluir o usuário "${u.name}"? O cadastro será removido dos controladores e do sistema.`)) return;
                              withReload(() => api.deleteUser(u.id), "Falha ao excluir.");
                            }}
                          >
                            Excluir
                          </button>
                        </>
                      )}
                      <Link className="btn btn-outline btn-sm" to={`/users/${u.id}`}>
                        Perfil
                      </Link>
                      <button className="btn btn-outline btn-sm" onClick={() => toggleHistory(u.id)}>
                        Histórico
                      </button>
                    </div>
                  </td>
                </tr>
                {editingId === u.id && canEdit && (
                  <tr className="row-expand">
                    <td colSpan={8}>
                      <div className="expand-panel">
                        <h4 style={{ marginTop: 0 }}>Editando: {u.name}</h4>
                        <UserForm mode="edit" userId={u.id} groups={groups} controllers={controllers} onSaved={afterEdit} onCancel={() => setEditingId(null)} />
                      </div>
                    </td>
                  </tr>
                )}
                {historyUserId === u.id && (
                  <tr className="row-expand">
                    <td colSpan={8}>
                      <div className="expand-panel">
                        {historyEntries.length === 0 ? (
                          <p className="text-muted" style={{ margin: 0 }}>
                            Sem registros de histórico.
                          </p>
                        ) : (
                          <ul style={{ margin: 0, paddingLeft: "1.1rem" }}>
                            {historyEntries.map((entry, i) => (
                              <li key={i}>
                                {new Date(entry.timestampUtc).toLocaleString()} — <strong>{entry.action}</strong> (
                                {entry.performedByUsername ?? "sistema"}){entry.details ? ` — ${entry.details}` : ""}
                              </li>
                            ))}
                          </ul>
                        )}
                      </div>
                    </td>
                  </tr>
                )}
              </Fragment>
            ))}
            {users.length === 0 && (
              <tr>
                <td colSpan={8} className="text-muted">
                  {total === 0 && !debouncedSearch && !groupFilter ? "Nenhum usuário cadastrado." : "Nenhum usuário encontrado com os filtros atuais."}
                </td>
              </tr>
            )}
          </tbody>
        </table>
      </div>

      <div className="btn-group" style={{ marginTop: "0.75rem", alignItems: "center" }}>
        <button className="btn btn-outline btn-sm" disabled={page <= 1} onClick={() => setPage(page - 1)}>
          Anterior
        </button>
        <span className="text-muted" style={{ alignSelf: "center" }}>
          página {page} de {totalPages} · {total} usuário{total === 1 ? "" : "s"}
        </span>
        <button className="btn btn-outline btn-sm" disabled={page >= totalPages} onClick={() => setPage(page + 1)}>
          Próxima
        </button>
      </div>
    </div>
  );
}
