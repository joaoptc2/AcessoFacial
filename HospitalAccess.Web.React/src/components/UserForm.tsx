import { useEffect, useMemo, useState, type FormEvent } from "react";
import { api, ApiError, type ControllerDto, type UserGroupDto } from "../lib/api";
import { downscaleImageForUpload } from "../lib/image";
import { ControllerChecklist } from "./ControllerChecklist";

interface UserFormProps {
  mode: "create" | "edit";
  /** Obrigatório em mode="edit". */
  userId?: string;
  groups: UserGroupDto[];
  controllers: ControllerDto[];
  onSaved: () => void;
  onCancel: () => void;
}

interface FormModel {
  name: string;
  timeGroup: number;
  groupId: string;
  cardNumber: string;
}

const emptyForm: FormModel = { name: "", timeGroup: 1, groupId: "", cardNumber: "" };

/**
 * Formulário de usuário reutilizado pelo cadastro (painel recolhível) e pela edição inline (na
 * própria linha da tabela). As portas do grupo escolhido aparecem travadas na lista (herdadas);
 * o operador ajusta só as portas extras. O servidor separa herdadas de manuais ao salvar.
 */
export function UserForm({ mode, userId, groups, controllers, onSaved, onCancel }: UserFormProps) {
  const [form, setForm] = useState<FormModel>(emptyForm);
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [photo, setPhoto] = useState<File | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [loading, setLoading] = useState(mode === "edit");

  useEffect(() => {
    if (mode !== "edit" || !userId) return;
    let active = true;
    (async () => {
      try {
        const detail = await api.getUser(userId);
        if (!active) return;
        setForm({
          name: detail.name,
          timeGroup: detail.timeGroup,
          groupId: detail.groupId ?? "",
          cardNumber: detail.cardNumber?.toString() ?? "",
        });
        setSelected(new Set(detail.controllerIds));
      } catch (err) {
        if (active) setError(err instanceof ApiError ? err.message : "Falha ao carregar o usuário.");
      } finally {
        if (active) setLoading(false);
      }
    })();
    return () => {
      active = false;
    };
  }, [mode, userId]);

  // Portas herdadas do grupo escolhido: travadas na lista (não podem ser removidas individualmente).
  const lockedIds = useMemo(() => {
    const group = groups.find((g) => g.id === form.groupId);
    return new Set(group?.defaultControllerIds ?? []);
  }, [groups, form.groupId]);

  function onGroupChange(newGroupId: string) {
    const oldDoors = groups.find((g) => g.id === form.groupId)?.defaultControllerIds ?? [];
    const newDoors = groups.find((g) => g.id === newGroupId)?.defaultControllerIds ?? [];
    setSelected((prev) => {
      const next = new Set(prev);
      for (const id of oldDoors) next.delete(id); // troca as portas do grupo anterior...
      for (const id of newDoors) next.add(id); // ...pelas do novo grupo (travadas).
      return next;
    });
    setForm((f) => ({ ...f, groupId: newGroupId }));
  }

  async function handleSubmit(e: FormEvent) {
    e.preventDefault();
    setError(null);

    if (mode === "create" && photo === null) {
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
    // Garante que as portas do grupo (travadas) sempre vão junto, mesmo para cadastros antigos
    // que ainda não as tinham como permissão.
    for (const id of new Set([...selected, ...lockedIds])) formData.append("ControllerIds", id);
    // Reduz a foto no navegador antes de enviar (evita o 413 do proxy com JPEG cru de celular).
    if (photo) formData.append("facePhoto", await downscaleImageForUpload(photo));

    try {
      if (mode === "create") await api.createUser(formData);
      else await api.updateUser(userId!, formData);
      onSaved();
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Falha inesperada ao salvar.");
    } finally {
      setBusy(false);
    }
  }

  if (loading) return <p className="text-muted" style={{ margin: 0 }}>Carregando...</p>;

  return (
    <form className="user-form" onSubmit={handleSubmit}>
      <div className="form-grid">
        <div className="form-field" style={{ flex: "2 1 240px" }}>
          <label>Nome</label>
          <input value={form.name} onChange={(e) => setForm({ ...form, name: e.target.value })} required />
        </div>
        <div className="form-field">
          <label>Grupo de horário (1-64)</label>
          <input
            type="number"
            min={1}
            max={64}
            value={form.timeGroup}
            onChange={(e) => setForm({ ...form, timeGroup: Number(e.target.value) })}
          />
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
        <div className="form-field">
          <label>Cartão Mifare/IC (opcional)</label>
          <input
            value={form.cardNumber}
            onChange={(e) => setForm({ ...form, cardNumber: e.target.value })}
            placeholder="Só se usar cartão"
          />
        </div>
      </div>

      <div className="form-field" style={{ marginTop: "0.75rem" }}>
        <label>Portas com acesso permitido</label>
        {form.groupId && (
          <p className="text-muted" style={{ fontSize: "0.8rem", margin: "0.1rem 0 0.35rem" }}>
            As portas marcadas como <span className="tag">grupo</span> são herdadas do grupo e geridas por ele — ajuste
            aqui só as portas extras.
          </p>
        )}
        <ControllerChecklist controllers={controllers} selected={selected} onChange={setSelected} lockedIds={lockedIds} />
      </div>

      <div className="form-field" style={{ marginTop: "0.75rem" }}>
        <label>Foto de face (JPG){mode === "edit" ? " — em branco mantém a atual" : ""}</label>
        <input type="file" accept="image/jpeg" onChange={(e) => setPhoto(e.target.files?.[0] ?? null)} />
      </div>

      {error && <div className="alert alert-danger">{error}</div>}

      <div className="btn-group" style={{ marginTop: "0.75rem" }}>
        <button type="submit" className="btn btn-primary" disabled={busy}>
          {busy ? "Salvando..." : mode === "create" ? "Cadastrar" : "Salvar"}
        </button>
        <button type="button" className="btn btn-outline" onClick={onCancel} disabled={busy}>
          Cancelar
        </button>
      </div>
    </form>
  );
}
