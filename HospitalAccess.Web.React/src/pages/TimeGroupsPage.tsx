import { useEffect, useMemo, useState, type FormEvent } from "react";
import { api, ApiError, type TimeGroupScheduleDto } from "../lib/api";

const WEEKDAY_NAMES = ["Segunda", "Terça", "Quarta", "Quinta", "Sexta", "Sábado", "Domingo"];

interface DayModel {
  enabled: boolean;
  begin: string;
  end: string;
}

interface FormModel {
  groupNumber: number;
  name: string;
  days: DayModel[];
}

function emptyDays(): DayModel[] {
  return Array.from({ length: 7 }, () => ({ enabled: false, begin: "07:00", end: "19:00" }));
}

const emptyForm = (): FormModel => ({ groupNumber: 1, name: "", days: emptyDays() });

export function TimeGroupsPage() {
  const [schedules, setSchedules] = useState<TimeGroupScheduleDto[]>([]);
  const [form, setForm] = useState<FormModel>(emptyForm());
  const [editingId, setEditingId] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [syncing, setSyncing] = useState(false);
  const [syncMessage, setSyncMessage] = useState<string | null>(null);
  const [search, setSearch] = useState("");

  const load = async () => setSchedules(await api.getTimeGroups());

  useEffect(() => {
    load();
  }, []);

  const filtered = useMemo(
    () =>
      schedules.filter(
        (t) => !search || t.name.toLowerCase().includes(search.toLowerCase()) || String(t.groupNumber).includes(search),
      ),
    [schedules, search],
  );

  function updateDay(index: number, patch: Partial<DayModel>) {
    setForm((prev) => {
      const days = [...prev.days];
      days[index] = { ...days[index], ...patch };
      return { ...prev, days };
    });
  }

  function startEdit(t: TimeGroupScheduleDto) {
    setEditingId(t.id);
    const days = emptyDays();
    for (const seg of t.segments.filter((s) => s.segmentIndex === 0)) {
      days[seg.weekday] = { enabled: true, begin: seg.beginTime.slice(0, 5), end: seg.endTime.slice(0, 5) };
    }
    setForm({ groupNumber: t.groupNumber, name: t.name, days });
  }

  function cancelEdit() {
    setEditingId(null);
    setForm(emptyForm());
  }

  async function handleSave(e: FormEvent) {
    e.preventDefault();
    setError(null);
    const segments = form.days
      .map((d, weekday) => ({ d, weekday }))
      .filter((x) => x.d.enabled)
      .map((x) => ({
        weekday: x.weekday,
        segmentIndex: 0,
        beginTime: `${x.d.begin}:00`,
        endTime: `${x.d.end}:00`,
      }));

    try {
      if (editingId === null) await api.createTimeGroup({ groupNumber: form.groupNumber, name: form.name, segments });
      else await api.updateTimeGroup(editingId, { groupNumber: form.groupNumber, name: form.name, segments });
      cancelEdit();
      await load();
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Falha inesperada ao salvar.");
    }
  }

  async function handleDelete(id: string) {
    setError(null);
    try {
      await api.deleteTimeGroup(id);
      await load();
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Falha inesperada ao excluir.");
    }
  }

  async function handleSyncAll() {
    setSyncing(true);
    setSyncMessage(null);
    try {
      const result = await api.syncAllTimeGroups();
      setSyncMessage(`Sincronização disparada em segundo plano para ${result.controllerCount} controlador(es).`);
    } catch (err) {
      setSyncMessage(`Falha: ${err instanceof ApiError ? err.message : "erro inesperado"}`);
    } finally {
      setSyncing(false);
    }
  }

  return (
    <div>
      <h2>Grade horária</h2>
      <p className="text-muted">
        Define o que cada grupo de horário (1-64, usado no cadastro de usuários) significa em termos de dias/horários de
        acesso. O dispositivo suporta até 8 janelas por dia; esta tela simplifica para uma janela por dia — abertura mais
        granular pode ser feita via API.
      </p>

      <form className="card" style={{ marginBottom: "1rem" }} onSubmit={handleSave}>
        <div className="form-row" style={{ marginBottom: "0.75rem" }}>
          <div className="form-field" style={{ minWidth: 90 }}>
            <label>Grupo (1-64)</label>
            <input type="number" value={form.groupNumber} onChange={(e) => setForm({ ...form, groupNumber: Number(e.target.value) })} />
          </div>
          <div className="form-field" style={{ minWidth: 260 }}>
            <label>Nome</label>
            <input
              value={form.name}
              onChange={(e) => setForm({ ...form, name: e.target.value })}
              placeholder="Ex.: Dias úteis 7h-19h"
              required
            />
          </div>
        </div>

        <table style={{ marginBottom: "0.75rem" }}>
          <thead>
            <tr>
              <th>Dia</th>
              <th>Ativo</th>
              <th>Início</th>
              <th>Fim</th>
            </tr>
          </thead>
          <tbody>
            {form.days.map((day, i) => (
              <tr key={i}>
                <td>{WEEKDAY_NAMES[i]}</td>
                <td>
                  <input type="checkbox" checked={day.enabled} onChange={(e) => updateDay(i, { enabled: e.target.checked })} />
                </td>
                <td>
                  <input type="time" value={day.begin} onChange={(e) => updateDay(i, { begin: e.target.value })} />
                </td>
                <td>
                  <input type="time" value={day.end} onChange={(e) => updateDay(i, { end: e.target.value })} />
                </td>
              </tr>
            ))}
          </tbody>
        </table>

        <div className="btn-group">
          <button type="submit" className="btn btn-primary">
            {editingId === null ? "Adicionar" : "Salvar"}
          </button>
          {editingId !== null && (
            <button type="button" className="btn btn-outline" onClick={cancelEdit}>
              Cancelar
            </button>
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
          <input placeholder="Nome do grupo..." value={search} onChange={(e) => setSearch(e.target.value)} />
        </div>
      </div>

      <table>
        <thead>
          <tr>
            <th>Grupo</th>
            <th>Nome</th>
            <th>Dias configurados</th>
            <th>Ações</th>
          </tr>
        </thead>
        <tbody>
          {filtered.map((t) => (
            <tr key={t.id}>
              <td>{t.groupNumber}</td>
              <td>{t.name}</td>
              <td>{t.segments.length}</td>
              <td>
                <div className="btn-group">
                  <button className="btn btn-outline btn-sm" onClick={() => startEdit(t)}>
                    Editar
                  </button>
                  <button className="btn btn-danger-outline btn-sm" onClick={() => handleDelete(t.id)}>
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
