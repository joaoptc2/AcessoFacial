import { useEffect, useState } from "react";
import { api, downloadBlob, type AlarmEventPage as AlarmEventPageData, type ControllerDto } from "../lib/api";

const PAGE_SIZE = 25;

export function AlarmEventsPage() {
  const [controllers, setControllers] = useState<ControllerDto[]>([]);
  const [from, setFrom] = useState("");
  const [to, setTo] = useState("");
  const [controllerId, setControllerId] = useState("");
  const [kind, setKind] = useState("");
  const [currentPage, setCurrentPage] = useState(1);
  const [page, setPage] = useState<AlarmEventPageData | null>(null);

  useEffect(() => {
    api.getControllers().then(setControllers);
    search();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  function params() {
    return {
      from: from ? new Date(from).toISOString() : undefined,
      to: to ? new Date(to).toISOString() : undefined,
      controllerId: controllerId || undefined,
      kind: kind || undefined,
    };
  }

  async function loadPage(pageNumber: number) {
    setCurrentPage(pageNumber);
    setPage(await api.queryAlarmEvents({ ...params(), page: pageNumber, pageSize: PAGE_SIZE }));
  }

  function search() {
    loadPage(1);
  }

  async function exportCsv() {
    const blob = await api.exportAlarmEventsCsv(params());
    downloadBlob(blob, `alarm-events-${new Date().toISOString().replace(/[-:.TZ]/g, "").slice(0, 14)}.csv`);
  }

  return (
    <div>
      <h2>Log de alarmes de hardware</h2>
      <p className="text-muted">Incêndio, coação, sabotagem, arrombamento, lista negra, timeout de abertura etc.</p>

      <div className="card form-row" style={{ marginBottom: "1rem" }}>
        <div className="form-field">
          <label>De</label>
          <input type="datetime-local" value={from} onChange={(e) => setFrom(e.target.value)} />
        </div>
        <div className="form-field">
          <label>Até</label>
          <input type="datetime-local" value={to} onChange={(e) => setTo(e.target.value)} />
        </div>
        <div className="form-field">
          <label>Controlador</label>
          <select value={controllerId} onChange={(e) => setControllerId(e.target.value)}>
            <option value="">(todos)</option>
            {controllers.map((c) => (
              <option key={c.id} value={c.id}>
                {c.name}
              </option>
            ))}
          </select>
        </div>
        <div className="form-field">
          <label>Tipo</label>
          <select value={kind} onChange={(e) => setKind(e.target.value)}>
            <option value="">(todos)</option>
            <option value="DoorSensor">Sensor de porta</option>
            <option value="Panic">Pânico</option>
            <option value="Fire">Incêndio</option>
            <option value="InvalidCard">Credencial inválida</option>
            <option value="Duress">Coação</option>
            <option value="Smoke">Fumaça</option>
            <option value="AntiTheft">Sabotagem</option>
            <option value="Blacklist">Lista negra</option>
            <option value="OpenDoorTimeout">Timeout de abertura</option>
          </select>
        </div>
        <div className="form-field">
          <button className="btn btn-primary" onClick={search}>
            Filtrar
          </button>
        </div>
        <div className="form-field">
          <button className="btn btn-outline" onClick={exportCsv}>
            Exportar CSV
          </button>
        </div>
      </div>

      {page && (
        <>
          <p className="text-muted">
            {page.total} registro(s) — página {page.page}
          </p>
          <table>
            <thead>
              <tr>
                <th>Data/Hora</th>
                <th>Controlador</th>
                <th>Tipo</th>
                <th>Código bruto</th>
                <th>Status</th>
              </tr>
            </thead>
            <tbody>
              {page.items.map((item) => (
                <tr key={item.id} style={item.cleared ? undefined : { background: "#fef2f2" }}>
                  <td>{new Date(item.timestampUtc).toLocaleString()}</td>
                  <td>{item.controllerName}</td>
                  <td>{item.kind}</td>
                  <td>{item.rawEventCode}</td>
                  <td>{item.cleared ? "Encerrado" : "Disparado"}</td>
                </tr>
              ))}
            </tbody>
          </table>

          <div className="btn-group" style={{ marginTop: "0.75rem" }}>
            <button className="btn btn-outline btn-sm" disabled={currentPage <= 1} onClick={() => loadPage(currentPage - 1)}>
              Anterior
            </button>
            <button
              className="btn btn-outline btn-sm"
              disabled={currentPage * PAGE_SIZE >= page.total}
              onClick={() => loadPage(currentPage + 1)}
            >
              Próxima
            </button>
          </div>
        </>
      )}
    </div>
  );
}
