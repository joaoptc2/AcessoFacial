import { useEffect, useMemo, useState, type FormEvent } from "react";
import { api, ApiError, type HolidayDto } from "../lib/api";

interface FormModel {
  index: number;
  name: string;
  date: string;
  repeatsYearly: boolean;
  holidayType: number;
}

const todayIso = () => new Date().toISOString().slice(0, 10);

const emptyForm = (): FormModel => ({ index: 1, name: "", date: todayIso(), repeatsYearly: true, holidayType: 1 });

export function HolidaysPage() {
  const [holidays, setHolidays] = useState<HolidayDto[]>([]);
  const [form, setForm] = useState<FormModel>(emptyForm());
  const [editingId, setEditingId] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [syncing, setSyncing] = useState(false);
  const [syncMessage, setSyncMessage] = useState<string | null>(null);
  const [search, setSearch] = useState("");

  const load = async () => setHolidays(await api.getHolidays());

  useEffect(() => {
    load();
  }, []);

  const filtered = useMemo(
    () => holidays.filter((h) => !search || h.name.toLowerCase().includes(search.toLowerCase())),
    [holidays, search],
  );

  function startEdit(h: HolidayDto) {
    setEditingId(h.id);
    setForm({
      index: h.index,
      name: h.name,
      date: h.date.slice(0, 10),
      repeatsYearly: h.repeatsYearly,
      holidayType: h.holidayType,
    });
  }

  function cancelEdit() {
    setEditingId(null);
    setForm(emptyForm());
  }

  async function handleSave(e: FormEvent) {
    e.preventDefault();
    setError(null);
    const body = {
      index: form.index,
      name: form.name,
      date: new Date(`${form.date}T00:00:00Z`).toISOString(),
      repeatsYearly: form.repeatsYearly,
      holidayType: form.holidayType,
    };
    try {
      if (editingId === null) await api.createHoliday(body);
      else await api.updateHoliday(editingId, body);
      cancelEdit();
      await load();
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Falha inesperada ao salvar.");
    }
  }

  async function handleDelete(id: string) {
    setError(null);
    try {
      await api.deleteHoliday(id);
      await load();
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Falha inesperada ao excluir.");
    }
  }

  async function handleSyncAll() {
    setSyncing(true);
    setSyncMessage(null);
    try {
      const result = await api.syncAllHolidays();
      setSyncMessage(`Sincronização disparada em segundo plano para ${result.controllerCount} controlador(es).`);
    } catch (err) {
      setSyncMessage(`Falha: ${err instanceof ApiError ? err.message : "erro inesperado"}`);
    } finally {
      setSyncing(false);
    }
  }

  return (
    <div>
      <h2>Feriados</h2>
      <p className="text-muted">Calendário de feriados (até 30 slots), empurrado para os controladores sob demanda.</p>

      <form className="card" style={{ marginBottom: "1rem" }} onSubmit={handleSave}>
        <div className="form-row">
          <div className="form-field" style={{ minWidth: 90 }}>
            <label>Slot (1-30)</label>
            <input type="number" value={form.index} onChange={(e) => setForm({ ...form, index: Number(e.target.value) })} />
          </div>
          <div className="form-field">
            <label>Nome</label>
            <input value={form.name} onChange={(e) => setForm({ ...form, name: e.target.value })} required />
          </div>
          <div className="form-field">
            <label>Data</label>
            <input type="date" value={form.date} onChange={(e) => setForm({ ...form, date: e.target.value })} />
          </div>
          <div className="form-field" style={{ flexDirection: "row", alignItems: "center", gap: "0.4rem" }}>
            <input
              type="checkbox"
              id="repeat"
              checked={form.repeatsYearly}
              onChange={(e) => setForm({ ...form, repeatsYearly: e.target.checked })}
            />
            <label htmlFor="repeat" style={{ margin: 0 }}>
              Repete todo ano
            </label>
          </div>
          <div className="form-field" style={{ minWidth: 70 }}>
            <label>Tipo</label>
            <input type="number" value={form.holidayType} onChange={(e) => setForm({ ...form, holidayType: Number(e.target.value) })} />
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
      </form>

      {error && <div className="alert alert-danger">{error}</div>}

      <button className="btn btn-outline btn-sm" onClick={handleSyncAll} disabled={syncing} style={{ marginBottom: "0.75rem" }}>
        {syncing ? "Sincronizando..." : "Aplicar em todos os controladores"}
      </button>
      {syncMessage && <p className="text-muted">{syncMessage}</p>}

      <div className="card" style={{ marginBottom: "1rem" }}>
        <div className="form-field">
          <label>Buscar</label>
          <input placeholder="Nome do feriado..." value={search} onChange={(e) => setSearch(e.target.value)} />
        </div>
      </div>

      <table>
        <thead>
          <tr>
            <th>Slot</th>
            <th>Nome</th>
            <th>Data</th>
            <th>Repete</th>
            <th>Tipo</th>
            <th>Ações</th>
          </tr>
        </thead>
        <tbody>
          {filtered.map((h) => (
            <tr key={h.id}>
              <td>{h.index}</td>
              <td>{h.name}</td>
              <td>{new Date(h.date).toLocaleDateString()}</td>
              <td>{h.repeatsYearly ? "Sim" : "Não"}</td>
              <td>{h.holidayType}</td>
              <td>
                <div className="btn-group">
                  <button className="btn btn-outline btn-sm" onClick={() => startEdit(h)}>
                    Editar
                  </button>
                  <button className="btn btn-danger-outline btn-sm" onClick={() => handleDelete(h.id)}>
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
