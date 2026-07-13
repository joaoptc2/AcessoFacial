import { useEffect, useMemo, useState, type FormEvent } from "react";
import { api, ApiError, type ControllerDto, type UserGroupDto } from "../lib/api";
import { ControllerChecklist } from "../components/ControllerChecklist";

interface FormModel {
  name: string;
  description: string;
}

const emptyForm: FormModel = { name: "", description: "" };

export function UserGroupsPage() {
  const [groups, setGroups] = useState<UserGroupDto[]>([]);
  const [controllers, setControllers] = useState<ControllerDto[]>([]);
  const [selectedDefaults, setSelectedDefaults] = useState<Set<string>>(new Set());
  const [form, setForm] = useState<FormModel>(emptyForm);
  const [editingId, setEditingId] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [search, setSearch] = useState("");

  const load = async () => {
    setGroups(await api.getUserGroups());
    setControllers(await api.getControllers());
  };

  useEffect(() => {
    load();
  }, []);

  const filtered = useMemo(
    () =>
      groups.filter(
        (g) =>
          !search ||
          g.name.toLowerCase().includes(search.toLowerCase()) ||
          (g.description ?? "").toLowerCase().includes(search.toLowerCase()),
      ),
    [groups, search],
  );

  function startEdit(g: UserGroupDto) {
    setEditingId(g.id);
    setForm({ name: g.name, description: g.description ?? "" });
    setSelectedDefaults(new Set(g.defaultControllerIds));
  }

  function cancelEdit() {
    setEditingId(null);
    setForm(emptyForm);
    setSelectedDefaults(new Set());
  }

  async function handleSave(e: FormEvent) {
    e.preventDefault();
    setError(null);
    const body = { name: form.name, description: form.description || null };
    try {
      let groupId: string;
      if (editingId === null) {
        const created = await api.createUserGroup(body);
        groupId = created.id;
      } else {
        await api.updateUserGroup(editingId, body);
        groupId = editingId;
      }
      await api.updateGroupDefaultControllers(groupId, Array.from(selectedDefaults));
      cancelEdit();
      await load();
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Falha inesperada ao salvar.");
    }
  }

  async function handleDelete(id: string) {
    setError(null);
    try {
      await api.deleteUserGroup(id);
      await load();
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Falha inesperada ao excluir.");
    }
  }

  return (
    <div>
      <h2>Grupos de usuários</h2>
      <p className="text-muted">
        Organização de usuários permanentes (ex.: "Enfermagem", "Manutenção"). As portas padrão definidas aqui são
        <strong> herdadas por todos os membros do grupo</strong>: adicionar ou remover uma porta aqui adiciona/remove
        em todos eles (e sincroniza no hardware). No cadastro do usuário ainda dá para incluir portas extras
        individuais, que não são afetadas por mudanças no grupo.
      </p>

      <form className="card" style={{ marginBottom: "1.25rem" }} onSubmit={handleSave}>
        <div className="form-row">
          <div className="form-field">
            <label>Nome</label>
            <input value={form.name} onChange={(e) => setForm({ ...form, name: e.target.value })} required />
          </div>
          <div className="form-field" style={{ minWidth: 260 }}>
            <label>Descrição</label>
            <input value={form.description} onChange={(e) => setForm({ ...form, description: e.target.value })} />
          </div>
          <div className="form-field">
            <button type="submit" className="btn btn-primary">
              {editingId === null ? "Adicionar" : "Salvar"}
            </button>
          </div>
          {editingId !== null && (
            <div className="form-field">
              <button type="button" className="btn btn-outline" onClick={cancelEdit}>
                Cancelar
              </button>
            </div>
          )}
        </div>
        <div className="form-field" style={{ marginTop: "0.75rem" }}>
          <label>Portas padrão do grupo (herdadas pelos membros)</label>
          <ControllerChecklist controllers={controllers} selected={selectedDefaults} onChange={setSelectedDefaults} />
        </div>
      </form>

      {error && <div className="alert alert-danger">{error}</div>}

      <div className="card" style={{ marginBottom: "1rem" }}>
        <div className="form-field">
          <label>Buscar</label>
          <input placeholder="Nome ou descrição..." value={search} onChange={(e) => setSearch(e.target.value)} />
        </div>
      </div>

      <table>
        <thead>
          <tr>
            <th>Nome</th>
            <th>Descrição</th>
            <th>Usuários</th>
            <th>Portas padrão</th>
            <th>Ações</th>
          </tr>
        </thead>
        <tbody>
          {filtered.map((g) => (
            <tr key={g.id}>
              <td>{g.name}</td>
              <td>{g.description}</td>
              <td>
                <span className="pill">{g.userCount}</span>
              </td>
              <td>
                <span className="pill">{g.defaultControllerIds.length}</span>
              </td>
              <td>
                <div className="btn-group">
                  <button className="btn btn-outline btn-sm" onClick={() => startEdit(g)}>
                    Editar
                  </button>
                  <button className="btn btn-danger-outline btn-sm" onClick={() => handleDelete(g.id)}>
                    Excluir
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
