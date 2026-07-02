import { useEffect, useMemo, useState, type FormEvent } from "react";
import { api, ApiError, type VisitorListItemDto } from "../lib/api";

function toLocalInputValue(date: Date): string {
  const pad = (n: number) => String(n).padStart(2, "0");
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}T${pad(date.getHours())}:${pad(date.getMinutes())}`;
}

function statusLabel(v: VisitorListItemDto): string {
  return v.isRevoked ? "Revogado" : v.isExpired ? "Expirado" : "Ativo";
}

function statusClass(v: VisitorListItemDto): string {
  return v.isRevoked ? "pill" : v.isExpired ? "pill pill-danger" : "pill pill-success";
}

export function VisitorsPage() {
  const [visitors, setVisitors] = useState<VisitorListItemDto[]>([]);
  const [name, setName] = useState("");
  const [validUntil, setValidUntil] = useState(() => toLocalInputValue(new Date(Date.now() + 24 * 60 * 60 * 1000)));
  const [timeGroup, setTimeGroup] = useState(1);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [qrImage, setQrImage] = useState<string | null>(null);
  const [qrVisitorName, setQrVisitorName] = useState("");
  const [search, setSearch] = useState("");
  const [statusFilter, setStatusFilter] = useState("");

  const load = async () => setVisitors(await api.getVisitors());

  useEffect(() => {
    load();
  }, []);

  const filtered = useMemo(
    () =>
      visitors.filter(
        (v) =>
          (!search || v.name.toLowerCase().includes(search.toLowerCase())) &&
          (!statusFilter || statusLabel(v).toLowerCase() === statusFilter.toLowerCase()),
      ),
    [visitors, search, statusFilter],
  );

  async function showQr(id: string, visitorName: string) {
    try {
      const blob = await api.generateVisitorQr(id);
      setQrVisitorName(visitorName);
      setQrImage(URL.createObjectURL(blob));
    } catch {
      setError("Falha ao gerar o QR.");
    }
  }

  async function handleCreate(e: FormEvent) {
    e.preventDefault();
    setError(null);
    setQrImage(null);

    const validUntilDate = new Date(validUntil);
    if (validUntilDate <= new Date()) {
      setError("Validade deve ser no futuro.");
      return;
    }
    if (timeGroup < 1 || timeGroup > 64) {
      setError("Grupo de horário deve estar entre 1 e 64.");
      return;
    }

    setBusy(true);
    try {
      const created = await api.createVisitor({ name, validUntil: validUntilDate.toISOString(), timeGroup });
      await showQr(created.id, name);
      setName("");
      await load();
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Falha inesperada ao cadastrar.");
    } finally {
      setBusy(false);
    }
  }

  async function handleRevoke(id: string) {
    setError(null);
    try {
      await api.revokeVisitor(id);
      await load();
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Falha inesperada ao revogar.");
    }
  }

  return (
    <div>
      <h2>Visitantes / temporários (acesso por QR)</h2>

      <form className="card" style={{ marginBottom: "1.25rem", maxWidth: 480 }} onSubmit={handleCreate}>
        <div className="form-field" style={{ marginBottom: "0.75rem" }}>
          <label>Nome</label>
          <input value={name} onChange={(e) => setName(e.target.value)} required />
        </div>
        <div className="form-field" style={{ marginBottom: "0.75rem" }}>
          <label>Válido até</label>
          <input type="datetime-local" value={validUntil} onChange={(e) => setValidUntil(e.target.value)} required />
        </div>
        <div className="form-field" style={{ marginBottom: "0.75rem" }}>
          <label>Grupo de horário (1-64)</label>
          <input type="number" value={timeGroup} onChange={(e) => setTimeGroup(Number(e.target.value))} />
        </div>
        {error && <div className="alert alert-danger">{error}</div>}
        <button type="submit" className="btn btn-primary" disabled={busy}>
          Cadastrar e gerar QR
        </button>
      </form>

      {qrImage && (
        <div className="card" style={{ marginBottom: "1.25rem", maxWidth: 340 }}>
          <h4 style={{ marginTop: 0 }}>QR de acesso — {qrVisitorName}</h4>
          <img src={qrImage} alt="QR de acesso" style={{ maxWidth: "100%" }} />
        </div>
      )}

      <div className="card form-row" style={{ marginBottom: "1rem" }}>
        <div className="form-field" style={{ minWidth: 260 }}>
          <label>Buscar</label>
          <input placeholder="Nome..." value={search} onChange={(e) => setSearch(e.target.value)} />
        </div>
        <div className="form-field">
          <label>Status</label>
          <select value={statusFilter} onChange={(e) => setStatusFilter(e.target.value)}>
            <option value="">(todos)</option>
            <option value="ativo">Ativo</option>
            <option value="expirado">Expirado</option>
            <option value="revogado">Revogado</option>
          </select>
        </div>
      </div>

      <table>
        <thead>
          <tr>
            <th>Nome</th>
            <th>Código</th>
            <th>Válido de</th>
            <th>Válido até</th>
            <th>Status</th>
            <th>Ações</th>
          </tr>
        </thead>
        <tbody>
          {filtered.map((v) => (
            <tr key={v.id}>
              <td>{v.name}</td>
              <td>{v.userCode}</td>
              <td>{v.validFrom ? new Date(v.validFrom).toLocaleString() : "—"}</td>
              <td>{v.validUntil ? new Date(v.validUntil).toLocaleString() : "—"}</td>
              <td>
                <span className={statusClass(v)}>{statusLabel(v)}</span>
              </td>
              <td>
                {!v.isRevoked && (
                  <div className="btn-group">
                    <button className="btn btn-outline btn-sm" onClick={() => showQr(v.id, v.name)}>
                      Ver QR
                    </button>
                    <button className="btn btn-danger-outline btn-sm" onClick={() => handleRevoke(v.id)}>
                      Revogar
                    </button>
                  </div>
                )}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}
