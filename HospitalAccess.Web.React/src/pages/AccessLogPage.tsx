import { useEffect, useState } from "react";
import { api, downloadBlob, type AccessLogPage as AccessLogPageData, type ControllerDto } from "../lib/api";

const PAGE_SIZE = 25;

export function AccessLogPage() {
  const [controllers, setControllers] = useState<ControllerDto[]>([]);
  const [from, setFrom] = useState("");
  const [to, setTo] = useState("");
  const [userCode, setUserCode] = useState("");
  const [controllerId, setControllerId] = useState("");
  const [method, setMethod] = useState("");
  const [currentPage, setCurrentPage] = useState(1);
  const [page, setPage] = useState<AccessLogPageData | null>(null);

  useEffect(() => {
    api.getControllers().then(setControllers);
    search();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  function params() {
    return {
      from: from ? new Date(from).toISOString() : undefined,
      to: to ? new Date(to).toISOString() : undefined,
      userCode: userCode ? Number(userCode) : undefined,
      controllerId: controllerId || undefined,
      method: method || undefined,
    };
  }

  async function loadPage(pageNumber: number) {
    setCurrentPage(pageNumber);
    setPage(await api.queryAccessLog({ ...params(), page: pageNumber, pageSize: PAGE_SIZE }));
  }

  function search() {
    loadPage(1);
  }

  async function exportCsv() {
    const blob = await api.exportAccessLogCsv(params());
    downloadBlob(blob, `access-log-${new Date().toISOString().replace(/[-:.TZ]/g, "").slice(0, 14)}.csv`);
  }

  return (
    <div>
      <h2>Log de Acessos (auditoria)</h2>

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
          <label>Código do usuário</label>
          <input type="number" value={userCode} onChange={(e) => setUserCode(e.target.value)} />
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
          <label>Método</label>
          <select value={method} onChange={(e) => setMethod(e.target.value)}>
            <option value="">(todos)</option>
            <option value="Face">Face</option>
            <option value="QrCode">QR Code</option>
            <option value="RemoteOpen">Abertura remota</option>
            <option value="Card">Cartão</option>
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
                <th>Data/Hora (UTC)</th>
                <th>Usuário</th>
                <th>Controlador</th>
                <th>Método</th>
                <th>Direção</th>
                <th>Concedido</th>
              </tr>
            </thead>
            <tbody>
              {page.items.map((item) => (
                <tr key={item.id} style={item.granted ? undefined : { background: "#fef2f2" }}>
                  <td>{new Date(item.timestampUtc).toLocaleString()}</td>
                  <td>{item.userName ?? item.userCode ?? "—"}</td>
                  <td>{item.controllerName}</td>
                  <td>{item.method}</td>
                  <td>{item.direction === 1 ? "Entrada" : item.direction === 2 ? "Saída" : "—"}</td>
                  <td>{item.granted ? "Sim" : "Não"}</td>
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
