import { useEffect, useMemo, useState } from "react";
import { api, ApiError, downloadBlob, type BedDto, type BedHistoryPage } from "../lib/api";
import { useAuth } from "../lib/AuthContext";
import { Icon } from "../components/Icon";
import { Modal } from "../components/Modal";
import { TvPanel } from "../components/TvPanel";
import { useFeedback } from "../lib/feedback";

const HISTORY_PAGE_SIZE = 15;

type Filtro = "todos" | "ocupados" | "livres";

const FILTROS: { key: Filtro; label: string }[] = [
  { key: "todos", label: "Todos" },
  { key: "ocupados", label: "Ocupados" },
  { key: "livres", label: "Livres" },
];

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

/** "há 3 dias", "há 4h" — o tempo de permanência é o que o operador lê primeiro. */
function desde(iso: string): string {
  const ms = Date.now() - new Date(iso).getTime();
  const horas = Math.floor(ms / 3_600_000);
  if (horas < 1) return "há menos de 1h";
  if (horas < 24) return `há ${horas}h`;
  const dias = Math.floor(horas / 24);
  return dias === 1 ? "há 1 dia" : `há ${dias} dias`;
}

/**
 * Gestão de leitos (1 leito = 1 quarto/porta): internar, transferir, alta — cada mudança pode
 * gerar o QR de acesso do paciente e disparar as boas-vindas no Home Assistant do quarto
 * (tela JPG gerada pelo servidor com o nome do paciente).
 *
 * Um CARTÃO por leito em vez de linhas de tabela: o painel de leitos é lido de relance
 * ("quem está onde, o que falta"), e cada leito carrega as suas próprias ações — incluindo as
 * do quarto (TV e a ação extra configurável, ex.: abrir o frigobar), que valem ocupado ou não.
 */
export function BedsPage() {
  const { confirm, toastSuccess } = useFeedback();
  // A Recepção não opera TV: a tela do quarto mostra o nome do paciente.
  const { role } = useAuth();
  const podeVerTv = role === "Admin" || role === "Operator";

  // null = ainda carregando (o cartão de "nenhum quarto" não pode piscar durante o load).
  const [beds, setBeds] = useState<BedDto[] | null>(null);
  // Leito cuja TV está aberta no painel (null = painel fechado).
  const [tvBed, setTvBed] = useState<BedDto | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  // Busca e filtro do painel
  const [busca, setBusca] = useState("");
  const [filtro, setFiltro] = useState<Filtro>("todos");

  // Formulário de internação (embutido no cartão do leito)
  const [admitBedId, setAdmitBedId] = useState<string | null>(null);
  const [admitName, setAdmitName] = useState("");
  const [admitValidUntil, setAdmitValidUntil] = useState("");

  // Transferência (embutida no cartão do leito)
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
      toastSuccess(
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
      toastSuccess(
        `${bed.patientName} transferido(a) para ${target?.name ?? "novo leito"}.` +
          (result.homeAssistantCalled ? " Boas-vindas enviadas à TV." : ""),
      );
    }, "Falha ao transferir.");
  }

  async function discharge(bed: BedDto) {
    if (!(await confirm({
      title: `Dar alta a ${bed.patientName}?`,
      text: `Leito ${bed.name} — o acesso do paciente é revogado nos controladores e o leito volta a ficar livre.`,
      confirmLabel: "Dar alta",
      danger: true,
    })))
      return;
    run(async () => {
      await api.dischargePatient(bed.controllerId);
      toastSuccess(`Alta registrada — acesso de ${bed.patientName} revogado.`);
    }, "Falha ao registrar a alta.");
  }

  /**
   * Na prática é o TESTE da automação do quarto. Por isso o aviso nomeia o script chamado
   * (script.BV_14): é com esse nome que se confere o que existe no Home Assistant, sem log.
   */
  function replayWelcome(bed: BedDto) {
    run(async () => {
      const result = await api.replayWelcome(bed.controllerId);
      if (result.homeAssistantCalled) {
        toastSuccess(
          `Boas-vindas reenviadas para a TV de ${bed.name}` +
            (result.service ? ` (${result.service}).` : "."),
        );
        return;
      }
      if (!result.service) {
        setError(
          `Não foi possível montar o nome do script para ${bed.name}. Confira o campo "Quarto no Home ` +
            `Assistant" do controlador (só letras, dígitos, _ e -) e o serviço em Configurações.`,
        );
        return;
      }
      setError(
        `A imagem foi gerada${result.welcomeImageUrl ? "" : " (verifique a imagem base em Configurações)"}, ` +
          `mas o Home Assistant não executou ${result.service}. Confira se esse script existe no HA e se a ` +
          `integração está ligada. Os Logs (Dev) trazem a resposta do HA.`,
      );
    }, "Falha ao reenviar boas-vindas.");
  }

  /** Ação extra configurável do quarto (ex.: abrir o frigobar). O rótulo vem do próprio leito. */
  function extraAction(bed: BedDto) {
    run(async () => {
      const result = await api.bedExtraAction(bed.controllerId);
      toastSuccess(`${result.label} — ${result.service} enviado a ${bed.name}.`);
    }, "Falha ao acionar o quarto.");
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

  function closeQr() {
    if (qrImage) URL.revokeObjectURL(qrImage);
    setQrImage(null);
    setQrBlob(null);
  }

  const todos = beds ?? [];
  const freeBeds = todos.filter((b) => !b.occupied);
  const ocupados = todos.length - freeBeds.length;

  // Depende de `beds` (a referência do estado), não do `todos` derivado: senão o memo nunca
  // acerta e o filtro reroda a cada tecla digitada em qualquer campo da tela.
  const visiveis = useMemo(() => {
    const termo = busca.trim().toLowerCase();
    return (beds ?? []).filter((b) => {
      if (filtro === "ocupados" && !b.occupied) return false;
      if (filtro === "livres" && b.occupied) return false;
      if (!termo) return true;
      return b.name.toLowerCase().includes(termo) || (b.patientName ?? "").toLowerCase().includes(termo);
    });
  }, [beds, busca, filtro]);

  function renderCard(bed: BedDto) {
    const internando = admitBedId === bed.controllerId;
    const transferindo = transferBedId === bed.controllerId;
    // TV e ação extra são do QUARTO, não da internação: valem com o leito livre também.
    const acoesDoQuarto = (podeVerTv && bed.tvIpAddress) || bed.extraActionLabel;

    return (
      <div key={bed.controllerId} className={`bed-card ${bed.occupied ? "is-occupied" : "is-free"}`}>
        <div className="bed-card-head">
          <Icon name="bed" size={17} />
          <span className="bed-name" title={bed.name}>
            {bed.name}
          </span>
          <span className={bed.occupied ? "pill pill-warning" : "pill pill-success"}>
            {bed.occupied ? "Ocupado" : "Livre"}
          </span>
        </div>

        <div className="bed-card-body">
          {bed.occupied ? (
            <>
              <div className="bed-patient">{bed.patientName}</div>
              <div className="bed-meta">
                Internado(a) {desde(bed.startedAtUtc!)} · {new Date(bed.startedAtUtc!).toLocaleString()}
              </div>
            </>
          ) : (
            <div className="bed-patient is-empty">Leito livre</div>
          )}

          <div className="bed-chips">
            {syncBadge(bed.accessSyncState)}
            {bed.homeAssistantRoomId && (
              <span className="tag is-literal" title="Quarto no Home Assistant">
                🏠 {bed.homeAssistantRoomId}
              </span>
            )}
            {bed.tvIpAddress && (
              <span
                className="tag is-literal"
                title={
                  bed.tvLastSeenUtc
                    ? `TV do quarto — vista pela última vez em ${new Date(bed.tvLastSeenUtc).toLocaleString()}`
                    : "TV do quarto — nunca respondeu"
                }
              >
                📺 {bed.tvIpAddress}
              </span>
            )}
          </div>

          {internando && (
            <div className="bed-form" style={{ marginTop: "0.9rem" }}>
              <div>
                <label htmlFor={`paciente-${bed.controllerId}`}>Nome do paciente</label>
                <input
                  id={`paciente-${bed.controllerId}`}
                  autoFocus
                  placeholder="Nome completo"
                  value={admitName}
                  onChange={(e) => setAdmitName(e.target.value)}
                />
              </div>
              <div>
                <label htmlFor={`validade-${bed.controllerId}`}>Validade do acesso (vazio = padrão)</label>
                <input
                  id={`validade-${bed.controllerId}`}
                  type="datetime-local"
                  value={admitValidUntil}
                  onChange={(e) => setAdmitValidUntil(e.target.value)}
                />
              </div>
            </div>
          )}

          {transferindo && (
            <div className="bed-form" style={{ marginTop: "0.9rem" }}>
              <div>
                <label htmlFor={`destino-${bed.controllerId}`}>Leito de destino</label>
                <select
                  id={`destino-${bed.controllerId}`}
                  autoFocus
                  value={transferTarget}
                  onChange={(e) => setTransferTarget(e.target.value)}
                >
                  <option value="">(escolha o leito)</option>
                  {freeBeds.map((b) => (
                    <option key={b.controllerId} value={b.controllerId}>
                      {b.name}
                    </option>
                  ))}
                </select>
              </div>
            </div>
          )}
        </div>

        <div className="bed-card-foot">
          {internando ? (
            <div className="btn-group">
              <button className={`btn btn-primary btn-sm${busy ? " is-busy" : ""}`} disabled={busy} onClick={() => admit(bed)}>
                Confirmar internação
              </button>
              <button className="btn btn-outline btn-sm" disabled={busy} onClick={() => setAdmitBedId(null)}>
                Cancelar
              </button>
            </div>
          ) : transferindo ? (
            <div className="btn-group">
              <button className={`btn btn-primary btn-sm${busy ? " is-busy" : ""}`} disabled={busy} onClick={() => transfer(bed)}>
                Confirmar transferência
              </button>
              <button className="btn btn-outline btn-sm" disabled={busy} onClick={() => setTransferBedId(null)}>
                Cancelar
              </button>
            </div>
          ) : (
            <>
              <div className="btn-group">
                {!bed.occupied ? (
                  <button
                    className="btn btn-primary btn-sm"
                    disabled={busy}
                    onClick={() => {
                      setAdmitBedId(bed.controllerId);
                      setTransferBedId(null);
                      setAdmitName("");
                      setAdmitValidUntil("");
                    }}
                  >
                    Internar paciente
                  </button>
                ) : (
                  <>
                    <button
                      className="btn btn-outline btn-sm"
                      disabled={busy || freeBeds.length === 0}
                      title={freeBeds.length === 0 ? "Não há leito livre para transferir" : undefined}
                      onClick={() => {
                        setTransferBedId(bed.controllerId);
                        setAdmitBedId(null);
                        setTransferTarget("");
                      }}
                    >
                      Transferir
                    </button>
                    <button className="btn btn-danger-outline btn-sm" disabled={busy} onClick={() => discharge(bed)}>
                      Alta
                    </button>
                  </>
                )}
              </div>

              {bed.occupied && (
                <div className="btn-group">
                  <button className="btn btn-outline btn-sm" disabled={busy || !bed.visitorUserId} onClick={() => showQr(bed)}>
                    Ver QR
                  </button>
                  <button className="btn btn-outline btn-sm" disabled={busy} onClick={() => replayWelcome(bed)}>
                    Reexibir boas-vindas
                  </button>
                </div>
              )}

              {acoesDoQuarto && (
                <div className="btn-group">
                  {bed.extraActionLabel && (
                    <button
                      className="btn btn-outline btn-sm"
                      disabled={busy}
                      title={`Aciona o script do quarto no Home Assistant (${bed.homeAssistantRoomId})`}
                      onClick={() => extraAction(bed)}
                    >
                      {bed.extraActionLabel}
                    </button>
                  )}
                  {podeVerTv && bed.tvIpAddress && (
                    <button
                      className="btn btn-outline btn-sm"
                      title={`Ver e operar a TV deste quarto (${bed.tvIpAddress})`}
                      onClick={() => setTvBed(bed)}
                    >
                      📺 TV
                    </button>
                  )}
                </div>
              )}
            </>
          )}
        </div>
      </div>
    );
  }

  return (
    <div>
      <div className="page-header">
        <div>
          <h2 style={{ marginBottom: "0.15rem" }}>Gestão de Leitos</h2>
          <p className="text-muted" style={{ margin: 0 }}>
            Internar cria o acesso do paciente (QR lido na porta do quarto) e manda as boas-vindas para a
            TV. Transferir move o acesso; a alta revoga.
          </p>
        </div>
      </div>

      {error && <div className="alert alert-danger">{error}</div>}

      {beds !== null && beds.length === 0 ? (
        <div className="card text-muted">
          Nenhum controlador está marcado como quarto/leito. Na tela <strong>Controladores / Portas</strong>,
          edite os controladores dos quartos e marque <strong>"É quarto/leito"</strong> — eles passam a
          aparecer aqui.
        </div>
      ) : (
        <>
          <div className="stat-grid">
            <div className="stat-card">
              <span className="stat-icon">
                <Icon name="bed" size={21} />
              </span>
              <div className="stat-body">
                <div className="stat-value">{todos.length}</div>
                <div className="stat-label">Leitos</div>
              </div>
            </div>
            <div className={`stat-card${ocupados > 0 ? " is-warning" : ""}`}>
              <span className="stat-icon">
                <Icon name="users" size={21} />
              </span>
              <div className="stat-body">
                <div className="stat-value">{ocupados}</div>
                <div className="stat-label">Ocupados</div>
              </div>
            </div>
            <div className="stat-card is-success">
              <span className="stat-icon">
                <Icon name="check" size={21} />
              </span>
              <div className="stat-body">
                <div className="stat-value">{freeBeds.length}</div>
                <div className="stat-label">Livres</div>
              </div>
            </div>
          </div>

          <div className="bed-toolbar">
            <input
              className="bed-search"
              placeholder="Buscar por leito ou paciente…"
              value={busca}
              onChange={(e) => setBusca(e.target.value)}
              aria-label="Buscar por leito ou paciente"
            />
            <div className="btn-group">
              {FILTROS.map((f) => (
                <button
                  key={f.key}
                  className={`btn btn-sm ${filtro === f.key ? "btn-primary" : "btn-outline"}`}
                  onClick={() => setFiltro(f.key)}
                >
                  {f.label}
                </button>
              ))}
            </div>
          </div>

          {visiveis.length === 0 ? (
            <div className="card text-muted">Nenhum leito corresponde à busca.</div>
          ) : (
            <div className="bed-grid">{visiveis.map(renderCard)}</div>
          )}
        </>
      )}

      {tvBed && (
        <TvPanel
          controllerId={tvBed.controllerId}
          bedName={tvBed.name}
          tvIpAddress={tvBed.tvIpAddress}
          onClose={() => setTvBed(null)}
        />
      )}

      {qrImage && (
        <Modal title={`QR de acesso — ${qrPatient}`} onClose={closeQr}>
          <div style={{ textAlign: "center" }}>
            <img src={qrImage} alt={`QR de acesso de ${qrPatient}`} style={{ maxWidth: "260px", width: "100%" }} />
            <div className="btn-group" style={{ justifyContent: "center", marginTop: "0.75rem" }}>
              <button
                className="btn btn-outline btn-sm"
                onClick={() => {
                  if (qrBlob) downloadBlob(qrBlob, `qr-${qrPatient.replace(/[^\w.-]+/g, "_") || "paciente"}.png`);
                }}
              >
                Baixar PNG
              </button>
              <button className="btn btn-outline btn-sm" onClick={closeQr}>
                Fechar
              </button>
            </div>
          </div>
        </Modal>
      )}

      <div className="panel">
        <div className="panel-head">
          <Icon name="clock" size={18} />
          <h3>Histórico de mudanças de leito</h3>
          <select
            style={{ marginLeft: "auto", maxWidth: 220 }}
            value={historyBed}
            aria-label="Filtrar o histórico por leito"
            onChange={(e) => {
              setHistoryBed(e.target.value);
              loadHistory(1, e.target.value);
            }}
          >
            <option value="">(todos os leitos)</option>
            {todos.map((b) => (
              <option key={b.controllerId} value={b.controllerId}>
                {b.name}
              </option>
            ))}
          </select>
        </div>
        <div className="panel-body flush">
          {history && history.items.length === 0 ? (
            <div className="panel-empty">Nenhuma mudança de leito registrada ainda.</div>
          ) : (
            history && (
              <>
                <div className="table-wrap">
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
                </div>
                {history.total > HISTORY_PAGE_SIZE && (
                  <div className="btn-group" style={{ padding: "0.6rem 1rem" }}>
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
      </div>
    </div>
  );
}
