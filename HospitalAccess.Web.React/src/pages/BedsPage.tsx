import { useEffect, useState } from "react";
import { api, ApiError, downloadBlob, type BedDto, type BedHistoryPage } from "../lib/api";

const HISTORY_PAGE_SIZE = 15;

function syncBadge(state: string | null) {
  if (!state) return null;
  const style: Record<string, string> = {
    Synced: "pill pill-success",
    Pending: "pill pill-warning",
    Failed: "pill pill-danger",
    Revoked: "pill",
  };
  const label: Record<string, string> = {
    Synced: "acesso ok",
    Pending: "acesso sincronizando…",
    Failed: "acesso com erro",
    Revoked: "acesso revogado",
  };
  return <span className={style[state] ?? "pill"}>{label[state] ?? state}</span>;
}

/**
 * Gestão de leitos (1 leito = 1 quarto/porta): internar, transferir, alta — cada mudança pode
 * gerar o QR de acesso do paciente e disparar as boas-vindas no Home Assistant do quarto
 * (tela JPG gerada pelo servidor com o nome do paciente).
 */
export function BedsPage() {
  // null = ainda carregando (o cartão de "nenhum quarto" não pode piscar durante o load).
  const [beds, setBeds] = useState<BedDto[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  // Formulário de internação (inline, por leito)
  const [admitBedId, setAdmitBedId] = useState<string | null>(null);
  const [admitName, setAdmitName] = useState("");
  const [admitValidUntil, setAdmitValidUntil] = useState("");

  // Transferência (inline, por leito)
  const [transferBedId, setTransferBedId] = useState<string | null>(null);
  const [transferTarget, setTransferTarget] = useState("");

  // QR
  const [qrPatient, setQrPatient] = useState("");
  const [qrBlob, setQrBlob] = useState<Blob | null>(null);
  const [qrImage, setQrImage] = useState<string | null>(null);

  // Histórico
  const [history, setHistory] = useState<BedHistoryPage | null>(null);
  const [historyBed, setHistoryBed] = useState("");
  const [historyPage, setHistoryPage] = useState(1);

  async function load() {
    try {
      setBeds(await api.getBeds());
      setError(null);
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Falha ao carregar os leitos.");
    }
  }

  async function loadHistory(pageNumber: number, bedId: string) {
    try {
      setHistoryPage(pageNumber);
      setHistory(await api.getBedHistory({ controllerId: bedId || undefined, page: pageNumber, pageSize: HISTORY_PAGE_SIZE }));
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Falha ao carregar o histórico.");
    }
  }

  useEffect(() => {
    load();
    loadHistory(1, "");
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  async function run(action: () => Promise<void>, failMsg: string) {
    setBusy(true);
    setError(null);
    setNotice(null);
    try {
      await action();
      await load();
      await loadHistory(historyPage, historyBed);
    } catch (err) {
      setError(err instanceof ApiError ? err.message : failMsg);
    } finally {
      setBusy(false);
    }
  }

  function admit(bed: BedDto) {
    const name = admitName.trim();
    if (!name) {
      setError("Informe o nome do paciente.");
      return;
    }
    run(async () => {
      const result = await api.admitPatient(bed.controllerId, {
        patientName: name,
        validUntil: admitValidUntil ? new Date(admitValidUntil).toISOString() : undefined,
      });
      setAdmitBedId(null);
      setAdmitName("");
      setAdmitValidUntil("");
      setNotice(
        `${name} internado(a) em ${bed.name}. Acesso em sincronização.` +
          (result.homeAssistantCalled ? " Boas-vindas enviadas à TV." : ""),
      );
    }, "Falha ao internar.");
  }

  function transfer(bed: BedDto) {
    if (!transferTarget) {
      setError("Escolha o leito de destino.");
      return;
    }
    const target = (beds ?? []).find((b) => b.controllerId === transferTarget);
    run(async () => {
      const result = await api.transferPatient(bed.controllerId, transferTarget);
      setTransferBedId(null);
      setTransferTarget("");
      setNotice(
        `${bed.patientName} transferido(a) para ${target?.name ?? "novo leito"}.` +
          (result.homeAssistantCalled ? " Boas-vindas enviadas à TV." : ""),
      );
    }, "Falha ao transferir.");
  }

  function discharge(bed: BedDto) {
    if (!window.confirm(`Dar ALTA a ${bed.patientName} (leito ${bed.name})? O acesso será revogado.`)) return;
    run(async () => {
      await api.dischargePatient(bed.controllerId);
      setNotice(`Alta registrada — acesso de ${bed.patientName} revogado.`);
    }, "Falha ao registrar a alta.");
  }

  function replayWelcome(bed: BedDto) {
    run(async () => {
      const result = await api.replayWelcome(bed.controllerId);
      setNotice(
        result.homeAssistantCalled
          ? `Boas-vindas reenviadas para a TV de ${bed.name}.`
          : `Imagem regenerada${result.welcomeImageUrl ? "" : " (verifique a configuração da imagem base)"} — HA não foi chamado (verifique HomeAssistantRoomId/HomeAssistant:Enabled).`,
      );
    }, "Falha ao reenviar boas-vindas.");
  }

  async function showQr(bed: BedDto) {
    if (!bed.visitorUserId) return;
    setError(null);
    try {
      const blob = await api.generateVisitorQr(bed.visitorUserId);
      setQrPatient(bed.patientName ?? "");
      setQrBlob(blob);
      setQrImage(URL.createObjectURL(blob));
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Falha ao obter o QR da controladora.");
    }
  }

  const freeBeds = (beds ?? []).filter((b) => !b.occupied);

  return (
    <div>
      <h2>Gestão de Leitos</h2>
      <p className="text-muted">
        Internação cria o acesso do paciente (QR lido da controladora do quarto) e dispara as
        boas-vindas no Home Assistant. Transferência move o acesso; alta revoga.
      </p>

      {error && <div className="alert alert-danger">{error}</div>}
      {notice && <div className="alert alert-success">{notice}</div>}

      {beds !== null && beds.length === 0 && (
        <div className="card text-muted" style={{ marginBottom: "1rem" }}>
          Nenhum controlador está marcado como quarto/leito. Na tela{" "}
          <strong>Controladores / Portas</strong>, edite os controladores dos quartos e marque{" "}
          <strong>"É quarto/leito"</strong> — eles passam a aparecer aqui.
        </div>
      )}

      <table>
        <thead>
          <tr>
            <th>Leito / Quarto</th>
            <th>Status</th>
            <th>Paciente</th>
            <th>Desde</th>
            <th>Ações</th>
          </tr>
        </thead>
        <tbody>
          {(beds ?? []).map((bed) => (
            <tr key={bed.controllerId}>
              <td>
                {bed.name}
                {bed.homeAssistantRoomId && (
                  <span className="text-muted" style={{ marginLeft: "0.4rem", fontSize: "0.78rem" }} title="Quarto integrado ao Home Assistant">
                    🏠 {bed.homeAssistantRoomId}
                  </span>
                )}
              </td>
              <td>
                {bed.occupied ? <span className="pill pill-warning">Ocupado</span> : <span className="pill pill-success">Livre</span>}{" "}
                {syncBadge(bed.accessSyncState)}
              </td>
              <td>{bed.patientName ?? "—"}</td>
              <td>{bed.startedAtUtc ? new Date(bed.startedAtUtc).toLocaleString() : "—"}</td>
              <td>
                {!bed.occupied ? (
                  admitBedId === bed.controllerId ? (
                    <div style={{ display: "flex", gap: "0.4rem", flexWrap: "wrap", alignItems: "center" }}>
                      <input
                        placeholder="Nome do paciente"
                        value={admitName}
                        onChange={(e) => setAdmitName(e.target.value)}
                        style={{ minWidth: "180px" }}
                      />
                      <input
                        type="datetime-local"
                        title="Validade do acesso (vazio = padrão configurado)"
                        value={admitValidUntil}
                        onChange={(e) => setAdmitValidUntil(e.target.value)}
                      />
                      <button className="btn btn-primary btn-sm" disabled={busy} onClick={() => admit(bed)}>
                        Confirmar
                      </button>
                      <button className="btn btn-outline btn-sm" disabled={busy} onClick={() => setAdmitBedId(null)}>
                        Cancelar
                      </button>
                    </div>
                  ) : (
                    <button
                      className="btn btn-primary btn-sm"
                      disabled={busy}
                      onClick={() => {
                        setAdmitBedId(bed.controllerId);
                        setAdmitName("");
                        setAdmitValidUntil("");
                      }}
                    >
                      Internar
                    </button>
                  )
                ) : transferBedId === bed.controllerId ? (
                  <div style={{ display: "flex", gap: "0.4rem", flexWrap: "wrap", alignItems: "center" }}>
                    <select value={transferTarget} onChange={(e) => setTransferTarget(e.target.value)}>
                      <option value="">(leito de destino)</option>
                      {freeBeds.map((b) => (
                        <option key={b.controllerId} value={b.controllerId}>
                          {b.name}
                        </option>
                      ))}
                    </select>
                    <button className="btn btn-primary btn-sm" disabled={busy} onClick={() => transfer(bed)}>
                      Confirmar
                    </button>
                    <button className="btn btn-outline btn-sm" disabled={busy} onClick={() => setTransferBedId(null)}>
                      Cancelar
                    </button>
                  </div>
                ) : (
                  <div className="btn-group" style={{ flexWrap: "wrap" }}>
                    <button
                      className="btn btn-outline btn-sm"
                      disabled={busy || freeBeds.length === 0}
                      title={freeBeds.length === 0 ? "Não há leito livre para transferir" : undefined}
                      onClick={() => {
                        setTransferBedId(bed.controllerId);
                        setTransferTarget("");
                      }}
                    >
                      Transferir
                    </button>
                    <button className="btn btn-outline btn-sm" disabled={busy || !bed.visitorUserId} onClick={() => showQr(bed)}>
                      Ver QR
                    </button>
                    <button className="btn btn-outline btn-sm" disabled={busy} onClick={() => replayWelcome(bed)}>
                      Reexibir boas-vindas
                    </button>
                    <button className="btn btn-danger-outline btn-sm" disabled={busy} onClick={() => discharge(bed)}>
                      Alta
                    </button>
                  </div>
                )}
              </td>
            </tr>
          ))}
        </tbody>
      </table>

      {qrImage && (
        <div className="card" style={{ marginTop: "1rem", maxWidth: "360px", textAlign: "center" }}>
          <h4 style={{ marginTop: 0 }}>QR de acesso — {qrPatient}</h4>
          <img src={qrImage} alt={`QR de acesso de ${qrPatient}`} style={{ maxWidth: "260px", width: "100%" }} />
          <div className="btn-group" style={{ justifyContent: "center", marginTop: "0.5rem" }}>
            <button
              className="btn btn-outline btn-sm"
              onClick={() => {
                if (qrBlob) downloadBlob(qrBlob, `qr-${qrPatient.replace(/[^\w.-]+/g, "_") || "paciente"}.png`);
              }}
            >
              Baixar PNG
            </button>
            <button
              className="btn btn-outline btn-sm"
              onClick={() => {
                if (qrImage) URL.revokeObjectURL(qrImage);
                setQrImage(null);
                setQrBlob(null);
              }}
            >
              Fechar
            </button>
          </div>
        </div>
      )}

      <h3 style={{ marginTop: "2rem" }}>Histórico de mudanças de leito</h3>
      <div className="card form-row" style={{ marginBottom: "1rem" }}>
        <div className="form-field">
          <label>Leito</label>
          <select
            value={historyBed}
            onChange={(e) => {
              setHistoryBed(e.target.value);
              loadHistory(1, e.target.value);
            }}
          >
            <option value="">(todos)</option>
            {(beds ?? []).map((b) => (
              <option key={b.controllerId} value={b.controllerId}>
                {b.name}
              </option>
            ))}
          </select>
        </div>
      </div>
      {history && history.items.length === 0 ? (
        <p className="text-muted">Nenhuma mudança de leito registrada ainda.</p>
      ) : (
        history && (
          <>
            <table>
              <thead>
                <tr>
                  <th>Paciente</th>
                  <th>Leito</th>
                  <th>Entrada</th>
                  <th>Saída</th>
                  <th>Motivo</th>
                  <th>Por</th>
                </tr>
              </thead>
              <tbody>
                {history.items.map((h) => (
                  <tr key={h.id}>
                    <td>{h.patientName}</td>
                    <td>{h.controllerName}</td>
                    <td>{new Date(h.startedAtUtc).toLocaleString()}</td>
                    <td>{new Date(h.endedAtUtc).toLocaleString()}</td>
                    <td>{h.endReason === "Transfer" ? "Transferência" : h.endReason === "Discharge" ? "Alta" : h.endReason}</td>
                    <td className="text-muted">{h.endedByUsername ?? "—"}</td>
                  </tr>
                ))}
              </tbody>
            </table>
            {history.total > HISTORY_PAGE_SIZE && (
              <div className="btn-group" style={{ marginTop: "0.5rem" }}>
                <button
                  className="btn btn-outline btn-sm"
                  disabled={historyPage <= 1}
                  onClick={() => loadHistory(historyPage - 1, historyBed)}
                >
                  Anterior
                </button>
                <span className="text-muted" style={{ alignSelf: "center" }}>
                  página {historyPage} de {Math.ceil(history.total / HISTORY_PAGE_SIZE)}
                </span>
                <button
                  className="btn btn-outline btn-sm"
                  disabled={historyPage >= Math.ceil(history.total / HISTORY_PAGE_SIZE)}
                  onClick={() => loadHistory(historyPage + 1, historyBed)}
                >
                  Próxima
                </button>
              </div>
            )}
          </>
        )
      )}
    </div>
  );
}
