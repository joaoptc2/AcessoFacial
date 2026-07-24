import { useEffect, useState, type FormEvent } from "react";
import { api, ApiError, type StaffRole, type StaffUserDto } from "../lib/api";
import { useAuth } from "../lib/AuthContext";

const ROLE_LABEL: Record<StaffRole, string> = {
  Admin: "Administrador",
  Operator: "Operador",
  Reception: "Recepção",
};

const ROLE_HINT: Record<StaffRole, string> = {
  Admin: "acesso total, inclusive configurações e usuários do sistema",
  Operator: "opera dispositivos, cadastros e sincronizações",
  Reception: "consulta e gestão de visitantes/leitos (sem configurações)",
};

/**
 * Usuários do SISTEMA (logins da aplicação) e seus cargos. Sem exclusão física: desativar
 * preserva a autoria histórica nas trilhas de auditoria. Mudança de cargo vale no próximo
 * login (o cargo viaja no token JWT). Só Admin.
 */
export function SystemUsersPage() {
  const { username: myUsername, role } = useAuth();

  const [users, setUsers] = useState<StaffUserDto[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [busyId, setBusyId] = useState<string | null>(null);

  const [newUsername, setNewUsername] = useState("");
  const [newPassword, setNewPassword] = useState("");
  const [newRole, setNewRole] = useState<StaffRole>("Reception");
  const [creating, setCreating] = useState(false);

  const isAdmin = role === "Admin";

  const load = async () => {
    try {
      setUsers(await api.getStaffUsers());
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Falha ao carregar os usuários do sistema.");
    }
  };

  useEffect(() => {
    if (isAdmin) load();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [isAdmin]);

  if (!isAdmin) {
    return <div className="alert alert-danger">Apenas administradores podem gerenciar os usuários do sistema.</div>;
  }

  async function handleCreate(e: FormEvent) {
    e.preventDefault();
    setError(null);
    setNotice(null);
    setCreating(true);
    try {
      await api.createStaffUser({ username: newUsername.trim(), password: newPassword, role: newRole });
      setNotice(`Usuário "${newUsername.trim()}" criado (${ROLE_LABEL[newRole]}).`);
      setNewUsername("");
      setNewPassword("");
      setNewRole("Reception");
      await load();
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Falha ao criar o usuário.");
    } finally {
      setCreating(false);
    }
  }

  async function changeRole(u: StaffUserDto, role: StaffRole) {
    if (role === u.role) return;
    setBusyId(u.id);
    setError(null);
    setNotice(null);
    try {
      await api.updateStaffUser(u.id, { role, active: u.active });
      setNotice(`Cargo de "${u.username}" alterado para ${ROLE_LABEL[role]} — vale no próximo login.`);
      await load();
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Falha ao alterar o cargo.");
    } finally {
      setBusyId(null);
    }
  }

  async function toggleActive(u: StaffUserDto) {
    const question = u.active
      ? `Desativar "${u.username}"? O usuário não conseguirá mais entrar no sistema.`
      : `Reativar "${u.username}"?`;
    if (!window.confirm(question)) return;
    setBusyId(u.id);
    setError(null);
    setNotice(null);
    try {
      await api.updateStaffUser(u.id, { role: u.role, active: !u.active });
      await load();
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Falha ao alterar o usuário.");
    } finally {
      setBusyId(null);
    }
  }

  async function resetPassword(u: StaffUserDto) {
    const password = window.prompt(`Nova senha para "${u.username}" (mínimo 8 caracteres):`);
    if (password === null) return;
    setBusyId(u.id);
    setError(null);
    setNotice(null);
    try {
      await api.resetStaffPassword(u.id, password);
      setNotice(`Senha de "${u.username}" redefinida.`);
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Falha ao redefinir a senha.");
    } finally {
      setBusyId(null);
    }
  }

  return (
    <div>
      <h2>Usuários do Sistema</h2>
      <p className="text-muted">
        Logins da aplicação e seus cargos. Mudanças de cargo valem no próximo login do usuário.
      </p>

      {error && <div className="alert alert-danger">{error}</div>}
      {notice && <div className="alert alert-success">{notice}</div>}

      <form className="card" style={{ marginBottom: "1.25rem" }} onSubmit={handleCreate}>
        <h3 style={{ marginTop: 0, fontSize: "1.05rem" }}>Novo usuário</h3>
        <div className="form-row">
          <div className="form-field">
            <label>Usuário</label>
            <input value={newUsername} onChange={(e) => setNewUsername(e.target.value)} required minLength={3} />
          </div>
          <div className="form-field">
            <label>Senha (mín. 8)</label>
            <input type="password" value={newPassword} onChange={(e) => setNewPassword(e.target.value)} required minLength={8} />
          </div>
          <div className="form-field" style={{ minWidth: 180 }}>
            <label>Cargo</label>
            <select value={newRole} onChange={(e) => setNewRole(e.target.value as StaffRole)}>
              {(Object.keys(ROLE_LABEL) as StaffRole[]).map((r) => (
                <option key={r} value={r}>
                  {ROLE_LABEL[r]}
                </option>
              ))}
            </select>
          </div>
          <div className="form-field">
            <button type="submit" className="btn btn-primary" disabled={creating}>
              Criar
            </button>
          </div>
        </div>
        <p className="text-muted" style={{ margin: "0.5rem 0 0", fontSize: "0.85rem" }}>
          {ROLE_LABEL[newRole]}: {ROLE_HINT[newRole]}.
        </p>
      </form>

      <table>
        <thead>
          <tr>
            <th>Usuário</th>
            <th>Cargo</th>
            <th>Situação</th>
            <th>Ações</th>
          </tr>
        </thead>
        <tbody>
          {users.map((u) => (
            <tr key={u.id} style={!u.active ? { background: "var(--surface-alt, #f8fafc)" } : undefined}>
              <td>
                {u.username} {u.username === myUsername && <span className="pill">você</span>}
              </td>
              <td>
                <select
                  value={u.role}
                  disabled={busyId === u.id}
                  onChange={(e) => changeRole(u, e.target.value as StaffRole)}
                  title={ROLE_HINT[u.role]}
                >
                  {(Object.keys(ROLE_LABEL) as StaffRole[]).map((r) => (
                    <option key={r} value={r}>
                      {ROLE_LABEL[r]}
                    </option>
                  ))}
                </select>
              </td>
              <td>
                <span className={u.active ? "pill pill-success" : "pill"}>{u.active ? "Ativo" : "Desativado"}</span>
              </td>
              <td>
                <div className="btn-group">
                  <button className="btn btn-outline btn-sm" disabled={busyId === u.id} onClick={() => resetPassword(u)}>
                    Redefinir senha
                  </button>
                  <button
                    className={u.active ? "btn btn-danger-outline btn-sm" : "btn btn-outline btn-sm"}
                    disabled={busyId === u.id}
                    onClick={() => toggleActive(u)}
                  >
                    {u.active ? "Desativar" : "Reativar"}
                  </button>
                </div>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}
