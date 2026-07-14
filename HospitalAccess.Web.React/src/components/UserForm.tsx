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
  document: string;
  employeeId: string;
  jobTitle: string;
  phone: string;
  email: string;
  notes: string;
}

const emptyForm: FormModel = {
  name: "",
  timeGroup: 1,
  groupId: "",
  cardNumber: "",
  document: "",
  employeeId: "",
  jobTitle: "",
  phone: "",
  email: "",
  notes: "",
};

/**
 * Formulário de usuário reutilizado pelo cadastro (painel recolhível) e pela edição inline (na
 * própria linha da tabela). As portas do grupo escolhido aparecem travadas na lista (herdadas);
 * o operador ajusta só as portas extras. O servidor separa herdadas de manuais ao salvar.
 */
export function UserForm({ mode, userId, groups, controllers, onSaved, onCancel }: UserFormProps) {
  const [form, setForm] = useState<FormModel>(emptyForm);
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [photo, setPhoto] = useState<File | null>(null);
  const [photoPreview, setPhotoPreview] = useState<string | null>(null);
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
          document: detail.document ?? "",
          employeeId: detail.employeeId ?? "",
          jobTitle: detail.jobTitle ?? "",
          phone: detail.phone ?? "",
          email: detail.email ?? "",
          notes: detail.notes ?? "",
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

  // Pré-visualização da foto: o novo arquivo escolhido ou, em edição sem novo arquivo, a foto atual.
  useEffect(() => {
    let active = true;
    let url: string | null = null;
    if (photo) {
      url = URL.createObjectURL(photo);
      setPhotoPreview(url);
      return () => {
        if (url) URL.revokeObjectURL(url);
      };
    }
    setPhotoPreview(null);
    if (mode === "edit" && userId) {
      (async () => {
        try {
          const blob = await api.getUserPhoto(userId);
          if (!active) return;
          url = URL.createObjectURL(blob);
          setPhotoPreview(url);
        } catch {
          /* sem foto cadastrada: sem preview */
        }
      })();
    }
    return () => {
      active = false;
      if (url) URL.revokeObjectURL(url);
    };
  }, [photo, mode, userId]);

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
    // Campos de perfil (só envia os preenchidos; ausente = limpo no servidor).
    const profile: Array<[string, string]> = [
      ["Document", form.document],
      ["EmployeeId", form.employeeId],
      ["JobTitle", form.jobTitle],
      ["Phone", form.phone],
      ["Email", form.email],
      ["Notes", form.notes],
    ];
    for (const [key, value] of profile) if (value.trim()) formData.append(key, value.trim());
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

      <div className="form-grid" style={{ marginTop: "0.75rem" }}>
        <div className="form-field">
          <label>Documento (CPF/RG)</label>
          <input value={form.document} onChange={(e) => setForm({ ...form, document: e.target.value })} />
        </div>
        <div className="form-field">
          <label>Matrícula</label>
          <input value={form.employeeId} onChange={(e) => setForm({ ...form, employeeId: e.target.value })} />
        </div>
        <div className="form-field">
          <label>Cargo / função</label>
          <input value={form.jobTitle} onChange={(e) => setForm({ ...form, jobTitle: e.target.value })} placeholder="Ex.: Enfermeiro" />
        </div>
        <div className="form-field">
          <label>Telefone</label>
          <input value={form.phone} onChange={(e) => setForm({ ...form, phone: e.target.value })} />
        </div>
        <div className="form-field" style={{ flex: "2 1 220px" }}>
          <label>E-mail</label>
          <input type="email" value={form.email} onChange={(e) => setForm({ ...form, email: e.target.value })} />
        </div>
      </div>

      <div className="form-field" style={{ marginTop: "0.75rem" }}>
        <label>Observações</label>
        <textarea
          rows={2}
          value={form.notes}
          onChange={(e) => setForm({ ...form, notes: e.target.value })}
          placeholder="Anotações internas (opcional)"
        />
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
        <div className="photo-field">
          {photoPreview && <img src={photoPreview} alt="Foto de face" className="photo-thumb" />}
          <input type="file" accept="image/jpeg" onChange={(e) => setPhoto(e.target.files?.[0] ?? null)} />
        </div>
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
