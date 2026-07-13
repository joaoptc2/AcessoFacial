import { Fragment, useEffect, useMemo, useState, type FormEvent } from "react";
import { api, ApiError, type ControllerDto, type UserAuditLogEntry, type UserGroupDto, type UserListItemDto } from "../lib/api";
import { downscaleImageForUpload } from "../lib/image";
import { useAuth } from "../lib/AuthContext";

interface FormModel {
  name: string;
  timeGroup: number;
  groupId: string;
  cardNumber: string;
}

const emptyForm: FormModel = { name: "", timeGroup: 1, groupId: "", cardNumber: "" };

export function UsersPage() {
  const { role } = useAuth();
  const canEdit = role === "Admin" || role === "Operator";

  const [users, setUsers] = useState<UserListItemDto[]>([]);
  const [groups, setGroups] = useState<UserGroupDto[]>([]);
  const [controllers, setControllers] = useState<ControllerDto[]>([]);
  const [selectedControllers, setSelectedControllers] = useState<Set<string>>(new Set());
  const [form, setForm] = useState<FormModel>(emptyForm);
  const [editingId, setEditingId] = useState<string | null>(null);
  const [originalName, setOriginalName] = useState("");
  const [photo, setPhoto] = useState<File | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [success, setSuccess] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [search, setSearch] = useState("");
  const [groupFilter, setGroupFilter] = useState("");
  const [historyUserId, setHistoryUserId] = useState<string | null>(null);
  const [historyEntries, setHistoryEntries] = useState<UserAuditLogEntry[]>([]);

  const load = async () => {
    setUsers(await api.getUsers());
    setGroups(await api.getUserGroups());
    setControllers(await api.getControllers());
  };

  useEffect(() => {
    load();
  }, []);

  const filtered = useMemo(
    () =>
      users.filter(
        (u) =>
          (!search || u.name.toLowerCase().includes(search.toLowerCase()) || String(u.userCode).includes(search)) &&
          (!groupFilter || u.groupId === groupFilter),
      ),
    [users, search, groupFilter],
  );

  function toggleController(id: string, checked: boolean) {
    setSelectedControllers((prev) => {
      const next = new Set(prev);
      if (checked) next.add(id);
      else next.delete(id);
      return next;
    });
  }

  function onGroupChange(groupId: string) {
    setForm({ ...form, groupId });
    const group = groups.find((g) => g.id === groupId);
    if (!group) return;
    setSelectedControllers((prev) => {
      const next = new Set(prev);
      for (const id of group.defaultControllerIds) next.add(id);
      return next;
    });
  }

  async function startEdit(id: string) {
    const detail = await api.getUser(id);
    setEditingId(id);
    setOriginalName(detail.name);
    setForm({
      name: detail.name,
      timeGroup: detail.timeGroup,
      groupId: detail.groupId ?? "",
      cardNumber: detail.cardNumber?.toString() ?? "",
    });
    setSelectedControllers(new Set(detail.controllerIds));
    setPhoto(null);
    setError(null);
    setSuccess(null);
  }

  function cancelEdit() {
    setEditingId(null);
    setForm(emptyForm);
    setSelectedControllers(new Set());
    setPhoto(null);
  }

  async function handleSave(e: FormEvent) {
    e.preventDefault();
    setError(null);
    setSuccess(null);

    if (editingId === null && photo === null) {
      setError("Selecione uma foto de face.");
      return;
    }
    if (form.timeGroup < 1 || form.timeGroup > 64) {
      setError("Grupo de horário deve estar entre 1 e 64.");
      return;
    }
    let cardNumber: number | null = null;
    if (form.cardNumber.trim()) {
      const parsed = Number(form.cardNumber);
      if (!Number.isFinite(parsed)) {
        setError("Número do cartão inválido.");
        return;
      }
      cardNumber = parsed;
    }

    setBusy(true);
    const formData = new FormData();
    formData.append("Name", form.name);
    formData.append("TimeGroup", String(form.timeGroup));
    if (form.groupId) formData.append("GroupId", form.groupId);
    if (cardNumber !== null) formData.append("CardNumber", String(cardNumber));
    for (const id of selectedControllers) formData.append("ControllerIds", id);
    // Reduz a foto no navegador antes de enviar (evita o 413 do proxy com JPEG cru de celular).
    if (photo) formData.append("facePhoto", await downscaleImageForUpload(photo));

    try {
      if (editingId === null) {
        await api.createUser(formData);
      } else {
        await api.updateUser(editingId, formData);
      }
      setSuccess(editingId === null ? "Usuário cadastrado e sincronização disparada." : "Usuário atualizado.");
      cancelEdit();
      await load();
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Falha inesperada ao salvar.");
    } finally {
      setBusy(false);
    }
  }

  async function handleDelete(id: string) {
    setError(null);
    try {
      await api.deleteUser(id);
      await load();
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Falha inesperada ao excluir.");
    }
  }

  async function handleRevoke(id: string) {
    setError(null);
    try {
      await api.revokeUser(id);
      await load();
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Falha inesperada ao revogar.");
    }
  }

  async function handleReactivate(id: string) {
    setError(null);
    try {
      await api.reactivateUser(id);
      await load();
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Falha inesperada ao reativar.");
    }
  }

  async function toggleHistory(id: string) {
    if (historyUserId === id) {
      setHistoryUserId(null);
      return;
    }
    setHistoryUserId(id);
    setHistoryEntries(await api.getUserAuditLog(id));
  }

  return (
    <div>
      <h2>Usuários permanentes (acesso por face)</h2>

      {canEdit ? (
        <form className="card" style={{ marginBottom: "1.25rem", maxWidth: 640 }} onSubmit={handleSave}>
          <h3 style={{ marginTop: 0, fontSize: "1.1rem" }}>{editingId === null ? "Novo usuário" : `Editando: ${originalName}`}</h3>
          <div className="form-field" style={{ marginBottom: "0.75rem" }}>
            <label>Nome</label>
            <input value={form.name} onChange={(e) => setForm({ ...form, name: e.target.value })} required />
          </div>
          <div className="form-row" style={{ marginBottom: "0.75rem" }}>
            <div className="form-field">
              <label>Grupo de horário do dispositivo (1-64)</label>
              <input type="number" value={form.timeGroup} onChange={(e) => setForm({ ...form, timeGroup: Number(e.target.value) })} />
            </div>
            <div className="form-field">
              <label>Grupo organizacional</label>
              <select value={form.groupId} onChange={(e) => onGroupChange(e.target.value)}>
                <option value="">(sem grupo)</option>
                {groups.map((g) => (
                  <option key={g.id} value={g.id}>
                    {g.name}
                  </option>
                ))}
              </select>
            </div>
          </div>
          <div className="form-field" style={{ marginBottom: "0.75rem" }}>
            <label>Número do cartão Mifare/IC (opcional)</label>
            <input
              value={form.cardNumber}
              onChange={(e) => setForm({ ...form, cardNumber: e.target.value })}
              placeholder="Deixe em branco se o usuário só usa face"
            />
          </div>
          <div style={{ marginBottom: "0.75rem" }}>
            <label style={{ fontSize: "0.82rem", fontWeight: 500, color: "var(--text-muted)" }}>Portas com acesso permitido</label>
            <p className="text-muted" style={{ fontSize: "0.82rem", margin: "0.2rem 0 0.4rem" }}>
              Ao escolher um grupo acima, as portas padrão dele são marcadas automaticamente — ajuste livremente antes de salvar.
            </p>
            <div style={{ display: "flex", flexWrap: "wrap", gap: "0.75rem" }}>
              {controllers.map((c) => (
                <label key={c.id} style={{ display: "flex", alignItems: "center", gap: "0.35rem", fontWeight: 400 }}>
                  <input
                    type="checkbox"
                    checked={selectedControllers.has(c.id)}
                    onChange={(e) => toggleController(c.id, e.target.checked)}
                  />
                  {c.name} ({c.ipAddress})
                </label>
              ))}
            </div>
          </div>
          <div className="form-field" style={{ marginBottom: "0.75rem" }}>
            <label>Foto de face (JPG){editingId !== null ? " — deixe em branco para manter a atual" : ""}</label>
            <input type="file" accept="image/jpeg" onChange={(e) => setPhoto(e.target.files?.[0] ?? null)} />
          </div>
          {error && <div className="alert alert-danger">{error}</div>}
          {success && <div className="alert alert-success">{success}</div>}
          <div className="btn-group">
            <button type="submit" className="btn btn-primary" disabled={busy}>
              {editingId === null ? "Cadastrar" : "Salvar"}
            </button>
            {editingId !== null && (
              <button type="button" className="btn btn-outline" onClick={cancelEdit}>
                Cancelar
              </button>
            )}
          </div>
        </form>
      ) : (
        <p className="text-muted">Seu papel (Recepção) tem acesso só de leitura a usuários permanentes.</p>
      )}

      <div className="card form-row" style={{ marginBottom: "1rem" }}>
        <div className="form-field" style={{ minWidth: 260 }}>
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
          {filtered.map((u) => (
            <Fragment key={u.id}>
              <tr style={u.revokedAtUtc ? { background: "#f8f9fc" } : undefined}>
                <td>{u.name}</td>
                <td>{u.userCode}</td>
                <td>{u.groupName ?? "—"}</td>
                <td>{u.cardNumber ?? "—"}</td>
                <td>{u.controllers.map((c) => c.controllerName).join(", ")}</td>
                <td>{u.hasFacePhoto ? "✔" : "—"}</td>
                <td>
                  <span className={u.revokedAtUtc ? "pill" : "pill pill-success"}>{u.revokedAtUtc ? "Revogado" : "Ativo"}</span>
                </td>
                <td>
                  <div className="btn-group">
                    {canEdit && (
                      <>
                        <button className="btn btn-outline btn-sm" onClick={() => startEdit(u.id)}>
                          Editar
                        </button>
                        {u.revokedAtUtc === null ? (
                          <button
                            className="btn btn-outline btn-sm"
                            style={{ borderColor: "#fde68a", color: "var(--warning)" }}
                            onClick={() => handleRevoke(u.id)}
                          >
                            Revogar
                          </button>
                        ) : (
                          <button
                            className="btn btn-outline btn-sm"
                            style={{ borderColor: "#bbf7d0", color: "var(--success)" }}
                            onClick={() => handleReactivate(u.id)}
                          >
                            Reativar
                          </button>
                        )}
                        <button className="btn btn-danger-outline btn-sm" onClick={() => handleDelete(u.id)}>
                          Excluir
                        </button>
                      </>
                    )}
                    <button className="btn btn-outline btn-sm" onClick={() => toggleHistory(u.id)}>
                      Histórico
                    </button>
                  </div>
                </td>
              </tr>
              {historyUserId === u.id && (
                <tr>
                  <td colSpan={8} style={{ background: "var(--surface-alt)" }}>
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
                  </td>
                </tr>
              )}
            </Fragment>
          ))}
        </tbody>
      </table>
    </div>
  );
}
