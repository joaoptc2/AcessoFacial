import { useEffect, useMemo, useState, type FormEvent } from "react";
import { api, ApiError, downloadBlob, type ControllerDto, type VisitorListItemDto } from "../lib/api";

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
  const [controllers, setControllers] = useState<ControllerDto[]>([]);
  const [name, setName] = useState("");
  const [validUntil, setValidUntil] = useState(() => toLocalInputValue(new Date(Date.now() + 24 * 60 * 60 * 1000)));
  const [timeGroup, setTimeGroup] = useState(1);
  const [selectedControllers, setSelectedControllers] = useState<string[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [qrImage, setQrImage] = useState<string | null>(null);
  const [qrBlob, setQrBlob] = useState<Blob | null>(null);
  const [qrVisitorName, setQrVisitorName] = useState("");
  const [search, setSearch] = useState("");
  const [statusFilter, setStatusFilter] = useState("");
  // QR copiado da controladora (o texto do campo "QRCode" que o aparelho valida).
  const [deviceQrText, setDeviceQrText] = useState("");
  const [deviceQrImage, setDeviceQrImage] = useState<string | null>(null);
  const [deviceQrBlob, setDeviceQrBlob] = useState<Blob | null>(null);
  const [deviceQrError, setDeviceQrError] = useState<string | null>(null);
  const [deviceQrBusy, setDeviceQrBusy] = useState(false);

  const load = async () => setVisitors(await api.getVisitors());

  useEffect(() => {
    load();
    api.getControllers().then(setControllers).catch(() => setControllers([]));
  }, []);

  function toggleController(id: string) {
    setSelectedControllers((prev) => (prev.includes(id) ? prev.filter((c) => c !== id) : [...prev, id]));
  }

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
      setQrBlob(blob);
      setQrImage(URL.createObjectURL(blob));
    } catch {
      setError("Falha ao gerar o QR.");
    }
  }

  function downloadQr() {
    if (!qrBlob) return;
    const safeName = qrVisitorName.replace(/[^\w.-]+/g, "_") || "visitante";
    downloadBlob(qrBlob, `qr-${safeName}.png`);
  }

  async function renderDeviceQr() {
    setDeviceQrError(null);
    const text = deviceQrText.trim();
    if (!text) {
      setDeviceQrError("Cole o texto do QRCode que aparece na controladora.");
      return;
    }
    setDeviceQrBusy(true);
    try {
      const blob = await api.renderQrFromText(text);
      setDeviceQrBlob(blob);
      setDeviceQrImage(URL.createObjectURL(blob));
    } catch (err) {
      setDeviceQrError(err instanceof ApiError ? err.message : "Falha ao gerar o QR.");
    } finally {
      setDeviceQrBusy(false);
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
    if (selectedControllers.length === 0) {
      setError("Selecione ao menos uma porta — sem porta o visitante não é enviado a nenhum controlador e o QR não abre nada.");
      return;
    }

    setBusy(true);
    try {
      const created = await api.createVisitor({
        name,
        validUntil: validUntilDate.toISOString(),
        timeGroup,
        controllerIds: selectedControllers,
      });
      await showQr(created.id, name);
      setName("");
      setSelectedControllers([]);
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

  async function handleDelete(id: string, visitorName: string) {
    setError(null);
    if (!window.confirm(`Excluir definitivamente o visitante "${visitorName}" e o QR? Remove a pessoa dos controladores e apaga o cadastro (não é reversível).`)) return;
    try {
      await api.deleteVisitor(id);
      await load();
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Falha inesperada ao excluir.");
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
        <div className="form-field" style={{ marginBottom: "0.75rem" }}>
          <label>Portas liberadas nesta visita</label>
          <p className="text-muted" style={{ margin: "0 0 0.4rem", fontSize: "0.85rem" }}>
            O visitante é cadastrado nessas portas com validade automática — o leitor abre pelo QR e o
            próprio controlador bloqueia após o vencimento.
          </p>
          {controllers.length === 0 ? (
            <span className="text-muted">Nenhum controlador cadastrado.</span>
          ) : (
            <div style={{ display: "flex", flexWrap: "wrap", gap: "0.5rem" }}>
              {controllers.map((c) => (
                <label key={c.id} style={{ display: "flex", alignItems: "center", gap: "0.3rem", margin: 0 }}>
                  <input
                    type="checkbox"
                    checked={selectedControllers.includes(c.id)}
                    onChange={() => toggleController(c.id)}
                  />
                  {c.name}
                </label>
              ))}
            </div>
          )}
        </div>
        {error && <div className="alert alert-danger">{error}</div>}
        <button type="submit" className="btn btn-primary" disabled={busy || selectedControllers.length === 0}>
          Cadastrar e gerar QR
        </button>
        {selectedControllers.length === 0 && (
          <p className="text-muted" style={{ fontSize: "0.8rem", marginBottom: 0 }}>
            Selecione ao menos uma porta para habilitar o cadastro.
          </p>
        )}
      </form>

      {qrImage && (
        <div className="card" style={{ marginBottom: "1.25rem", maxWidth: 360 }}>
          <h4 style={{ marginTop: 0 }}>QR de acesso — {qrVisitorName}</h4>
          {/* Fundo branco com folga: a "zona de silêncio" (moldura branca) é obrigatória para o
              leitor localizar o QR. image-rendering: pixelated evita que o navegador borre as
              bordas dos módulos ao redimensionar (QR borrado = leitura falha). */}
          <div style={{ background: "#fff", padding: 16, borderRadius: 8, display: "inline-block" }}>
            <img
              src={qrImage}
              alt="QR de acesso"
              style={{ display: "block", width: 260, height: 260, imageRendering: "pixelated" }}
            />
          </div>
          <div className="btn-group" style={{ marginTop: "0.6rem" }}>
            <button type="button" className="btn btn-primary btn-sm" onClick={downloadQr}>
              Baixar PNG
            </button>
          </div>
          <p className="text-muted" style={{ fontSize: "0.8rem", marginTop: "0.6rem", marginBottom: "0.4rem" }}>
            <strong>Imprima ou envie a imagem inteira, incluindo a moldura branca.</strong> Não recorte o QR
            rente às bordas — sem a margem branca o leitor não consegue lê-lo (aparece “QR inválido”). Prefira o
            <strong> Baixar PNG</strong> a tirar print da tela.
          </p>
          <p className="text-muted" style={{ fontSize: "0.8rem", marginBottom: 0 }}>
            O QR só abre a porta depois que o visitante é <strong>sincronizado</strong> nos controladores das
            portas escolhidas (feito automaticamente ao cadastrar). Se nenhuma porta foi selecionada, o QR
            não abrirá nada — edite/recadastre incluindo as portas.
          </p>
        </div>
      )}

      <div className="card" style={{ marginBottom: "1.25rem", maxWidth: 560, borderLeft: "3px solid var(--color-primary, #2f6fed)" }}>
        <h4 style={{ marginTop: 0 }}>QR da controladora (recomendado — este abre a porta)</h4>
        <p className="text-muted" style={{ marginTop: 0, fontSize: "0.85rem" }}>
          O leitor só aceita o QR que a <strong>própria controladora</strong> guardou para a pessoa. Abra o
          sistema web do controlador → cadastro do usuário → aba <strong>QR Code</strong>, copie o texto do campo
          <strong> QRCode</strong> e cole aqui. O sistema gera um PNG limpo e imprimível desse código.
        </p>
        <div className="form-field" style={{ marginBottom: "0.6rem" }}>
          <label>Texto do QRCode da controladora</label>
          <textarea
            value={deviceQrText}
            onChange={(e) => setDeviceQrText(e.target.value)}
            rows={2}
            placeholder="Ex.: dXNlcl9pZD0xX3RpbWU9MTc4MzA5NjA3OTgxMzIzOA=="
            style={{ width: "100%", fontFamily: "monospace", fontSize: "0.85rem" }}
          />
        </div>
        {deviceQrError && <div className="alert alert-danger">{deviceQrError}</div>}
        <button type="button" className="btn btn-primary btn-sm" onClick={renderDeviceQr} disabled={deviceQrBusy}>
          Gerar PNG
        </button>
        {deviceQrImage && (
          <div style={{ marginTop: "0.8rem" }}>
            <div style={{ background: "#fff", padding: 16, borderRadius: 8, display: "inline-block" }}>
              <img
                src={deviceQrImage}
                alt="QR da controladora"
                style={{ display: "block", width: 260, height: 260, imageRendering: "pixelated" }}
              />
            </div>
            <div className="btn-group" style={{ marginTop: "0.6rem" }}>
              <button
                type="button"
                className="btn btn-primary btn-sm"
                onClick={() => deviceQrBlob && downloadBlob(deviceQrBlob, "qr-controladora.png")}
              >
                Baixar PNG
              </button>
            </div>
            <p className="text-muted" style={{ fontSize: "0.8rem", marginBottom: 0 }}>
              Imprima/envie a imagem inteira (com a moldura branca). Não recorte.
            </p>
          </div>
        )}
      </div>

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
            <th>Sincronização (QR)</th>
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
                {v.syncTotal === 0 ? (
                  <span className="pill pill-danger" title="Sem porta — o QR não abre nada">sem porta</span>
                ) : v.syncSynced > 0 ? (
                  <span className="pill pill-success" title="QR ativo nas portas sincronizadas">
                    {v.syncSynced}/{v.syncTotal} porta(s)
                  </span>
                ) : (
                  <span className="pill pill-warning" title="Ainda não enviado ao(s) controlador(es)">
                    pendente {v.syncPending}/{v.syncTotal}
                  </span>
                )}
              </td>
              <td>
                <div className="btn-group">
                  {!v.isRevoked && (
                    <>
                      <button className="btn btn-outline btn-sm" onClick={() => showQr(v.id, v.name)}>
                        Ver QR
                      </button>
                      <button className="btn btn-danger-outline btn-sm" onClick={() => handleRevoke(v.id)}>
                        Revogar
                      </button>
                    </>
                  )}
                  <button className="btn btn-danger btn-sm" onClick={() => handleDelete(v.id, v.name)}>
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
