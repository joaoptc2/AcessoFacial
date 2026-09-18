import { useCallback, useEffect, useRef, useState } from "react";
import { api, ApiError, type TvStatusDto } from "../lib/api";
import { Modal } from "./Modal";
import { useAuth } from "../lib/AuthContext";
import { useFeedback } from "../lib/feedback";

/** Intervalo entre capturas. Não é vídeo: o aparelho leva ~0,5s só para comprimir o PNG. */
const REFRESH_MS = 1000;

/** Direcional do controle remoto. A ordem das linhas é a do controle físico. */
const DPAD: { key: string; label: string; title: string }[][] = [
  [{ key: "DPAD_UP", label: "▲", title: "Cima" }],
  [
    { key: "DPAD_LEFT", label: "◀", title: "Esquerda" },
    { key: "DPAD_CENTER", label: "OK", title: "Confirmar" },
    { key: "DPAD_RIGHT", label: "▶", title: "Direita" },
  ],
  [{ key: "DPAD_DOWN", label: "▼", title: "Baixo" }],
];

const EXTRAS: { key: string; label: string; title: string }[] = [
  { key: "BACK", label: "Voltar", title: "Voltar" },
  { key: "HOME", label: "Início", title: "Tela inicial" },
  { key: "DEL", label: "⌫", title: "Apagar caractere" },
  { key: "VOLUME_DOWN", label: "Vol −", title: "Abaixar volume" },
  { key: "VOLUME_UP", label: "Vol +", title: "Aumentar volume" },
];

function formatUptime(seconds: number | null | undefined): string {
  if (seconds == null) return "—";
  const total = Math.floor(seconds);
  const d = Math.floor(total / 86400);
  const h = Math.floor((total % 86400) / 3600);
  const m = Math.floor((total % 3600) / 60);
  if (d > 0) return `${d}d ${h}h`;
  if (h > 0) return `${h}h ${m}min`;
  return `${m}min`;
}

interface TvPanelProps {
  controllerId: string;
  bedName: string;
  tvIpAddress: string;
  onClose: () => void;
}

/**
 * Manutenção remota da TV de um quarto: ver a tela, operar o controle e digitar — sem subir.
 * O caso que motivou tudo é relogar um serviço de streaming cuja conta caiu.
 *
 * A tela NÃO é vídeo: é uma captura por segundo, buscada com o cabeçalho de autenticação e
 * exibida via object URL (um `<img src>` direto não levaria o token). A captura contém o nome
 * do paciente, então o intervalo só roda com o painel aberto — fechar interrompe de imediato.
 */
export function TvPanel({ controllerId, bedName, tvIpAddress, onClose }: TvPanelProps) {
  const { confirm } = useFeedback();
  const { role } = useAuth();
  const isAdmin = role === "Admin";

  const [status, setStatus] = useState<TvStatusDto | null>(null);
  const [screenUrl, setScreenUrl] = useState<string | null>(null);
  const [screenError, setScreenError] = useState<string | null>(null);
  const [text, setText] = useState("");
  const [busy, setBusy] = useState(false);
  const [notice, setNotice] = useState<string | null>(null);
  const [apps, setApps] = useState<string[] | null>(null);
  const [showMaintenance, setShowMaintenance] = useState(false);

  // Vivo enquanto o painel está montado — corta o laço de captura assim que fecha.
  const alive = useRef(true);
  // Object URL em exibição, para revogar o anterior: sem isso cada quadro vaza memória.
  const currentUrl = useRef<string | null>(null);

  const setScreen = useCallback((blob: Blob | null) => {
    if (currentUrl.current) URL.revokeObjectURL(currentUrl.current);
    currentUrl.current = blob ? URL.createObjectURL(blob) : null;
    setScreenUrl(currentUrl.current);
  }, []);

  // Status de abertura: é a chamada que entra na auditoria como "quem olhou este quarto".
  useEffect(() => {
    let cancelled = false;
    api
      .getTvStatus(controllerId)
      .then((s) => !cancelled && setStatus(s))
      .catch((e) => !cancelled && setStatus({
        online: false,
        state: "Erro",
        message: e instanceof ApiError ? e.message : "Falha ao consultar a TV.",
        model: null,
        uptimeSeconds: null,
        focus: null,
        detail: null,
      }));
    return () => {
      cancelled = true;
    };
  }, [controllerId]);

  // Laço de captura: encadeado por setTimeout (não setInterval) para nunca empilhar duas
  // requisições quando o aparelho demora a responder.
  useEffect(() => {
    alive.current = true;
    let timer: number | undefined;

    const tick = async () => {
      if (!alive.current) return;
      try {
        const blob = await api.getTvScreen(controllerId);
        if (!alive.current) return;
        setScreen(blob);
        setScreenError(null);
      } catch (e) {
        if (!alive.current) return;
        setScreenError(e instanceof ApiError ? e.message : "Não foi possível capturar a tela.");
      }
      if (alive.current) timer = window.setTimeout(tick, REFRESH_MS);
    };

    void tick();
    return () => {
      alive.current = false;
      if (timer) window.clearTimeout(timer);
      if (currentUrl.current) URL.revokeObjectURL(currentUrl.current);
      currentUrl.current = null;
    };
  }, [controllerId, setScreen]);

  const run = async (acao: () => Promise<void>, sucesso?: string) => {
    setBusy(true);
    setNotice(null);
    try {
      await acao();
      if (sucesso) setNotice(sucesso);
    } catch (e) {
      setNotice(e instanceof ApiError ? e.message : "Falha ao enviar o comando.");
    } finally {
      setBusy(false);
    }
  };

  const sendKey = (key: string) => run(() => api.sendTvKey(controllerId, key));

  const sendText = () =>
    run(async () => {
      await api.sendTvText(controllerId, text);
      setText("");
    }, "Texto enviado.");

  const loadApps = () =>
    run(async () => {
      setApps(await api.getTvApps(controllerId));
    });

  const scrcpyUrl = `scrcpy://${tvIpAddress}:5555`;

  return (
    <Modal title={`TV — ${bedName}`} onClose={onClose}>
      {/* Estado do aparelho */}
      <div className="card" style={{ marginBottom: "0.75rem" }}>
        {status === null ? (
          <span className="text-muted">Consultando a TV…</span>
        ) : status.online ? (
          <div style={{ display: "flex", gap: "0.75rem", flexWrap: "wrap", alignItems: "center" }}>
            <span className="pill pill-success">TV online</span>
            <span className="text-muted" style={{ fontSize: "0.82rem" }}>
              {status.model ?? "—"} · ligada há {formatUptime(status.uptimeSeconds)}
            </span>
            {status.focus && (
              <span className="text-muted" style={{ fontSize: "0.78rem" }} title="Tela em primeiro plano">
                em <code>{status.focus.split("/")[0]}</code>
              </span>
            )}
          </div>
        ) : (
          <div>
            <span className="pill pill-danger">TV indisponível</span>{" "}
            <span className="text-muted">{status.message}</span>
            {/* A frase original do adb é o que diferencia "falta instalar", "não alcanço o
                quarto" e "a chave não foi autorizada" — três correções distintas. */}
            {status.detail && (
              <div style={{ marginTop: "0.4rem" }}>
                <code style={{ fontSize: "0.75rem", overflowWrap: "anywhere" }}>{status.detail}</code>
              </div>
            )}
          </div>
        )}
      </div>

      {notice && <div className="card text-muted" style={{ marginBottom: "0.75rem" }}>{notice}</div>}

      <div style={{ display: "flex", gap: "1rem", flexWrap: "wrap", alignItems: "flex-start" }}>
        {/* Tela do quarto */}
        <div style={{ flex: "1 1 320px", minWidth: "260px" }}>
          {screenUrl ? (
            <img
              src={screenUrl}
              alt={`Tela da TV do ${bedName}`}
              style={{ width: "100%", borderRadius: "4px", background: "#000", display: "block" }}
            />
          ) : (
            <div
              className="card text-muted"
              style={{ aspectRatio: "16 / 9", display: "grid", placeItems: "center", textAlign: "center" }}
            >
              {screenError ?? "Capturando a tela…"}
            </div>
          )}
          {screenUrl && screenError && (
            <div className="text-muted" style={{ fontSize: "0.78rem", marginTop: "0.25rem" }}>
              Última captura falhou: {screenError}
            </div>
          )}
          <div className="text-muted" style={{ fontSize: "0.75rem", marginTop: "0.35rem" }}>
            Atualiza a cada segundo enquanto esta janela estiver aberta. Não é vídeo.{" "}
            <a href={scrcpyUrl} title="Abre o scrcpy instalado nesta máquina, se houver">
              abrir no scrcpy
            </a>
          </div>
        </div>

        {/* Controle remoto */}
        <div style={{ flex: "0 1 220px", minWidth: "200px" }}>
          <div style={{ display: "grid", gap: "0.3rem", justifyItems: "center" }}>
            {DPAD.map((linha, i) => (
              <div key={i} style={{ display: "flex", gap: "0.3rem" }}>
                {linha.map((b) => (
                  <button
                    key={b.key}
                    className="btn btn-outline btn-sm"
                    style={{ minWidth: "3rem" }}
                    disabled={busy}
                    title={b.title}
                    onClick={() => sendKey(b.key)}
                  >
                    {b.label}
                  </button>
                ))}
              </div>
            ))}
          </div>

          <div className="btn-group" style={{ flexWrap: "wrap", marginTop: "0.6rem" }}>
            {EXTRAS.map((b) => (
              <button
                key={b.key}
                className="btn btn-outline btn-sm"
                disabled={busy}
                title={b.title}
                onClick={() => sendKey(b.key)}
              >
                {b.label}
              </button>
            ))}
          </div>

          {/* Digitação — é o que resolve o login sem datilografar no direcional */}
          <div style={{ marginTop: "0.75rem" }}>
            <input
              id="tv-text"
              placeholder="Digitar no campo em foco"
              value={text}
              onChange={(e) => setText(e.target.value)}
              onKeyDown={(e) => {
                if (e.key === "Enter" && text) {
                  e.preventDefault();
                  void sendText();
                }
              }}
              style={{ width: "100%" }}
            />
            <div className="btn-group" style={{ marginTop: "0.4rem" }}>
              <button className="btn btn-primary btn-sm" disabled={busy || !text} onClick={sendText}>
                Enviar texto
              </button>
              <button className="btn btn-outline btn-sm" disabled={busy} onClick={() => sendKey("ENTER")}>
                ENTER
              </button>
              <button className="btn btn-outline btn-sm" disabled={busy} onClick={() => sendKey("TAB")}>
                TAB
              </button>
            </div>
            <div className="text-muted" style={{ fontSize: "0.73rem", marginTop: "0.3rem" }}>
              O que você digita aqui não é gravado em lugar nenhum — só o fato de ter digitado.
            </div>
          </div>
        </div>
      </div>

      {/* Manutenção — recolhida, porque no dia a dia não se usa */}
      <div style={{ marginTop: "1rem" }}>
        <button
          className="btn btn-outline btn-sm"
          onClick={() => {
            const abrindo = !showMaintenance;
            setShowMaintenance(abrindo);
            if (abrindo && apps === null) void loadApps();
          }}
        >
          {showMaintenance ? "Ocultar manutenção" : "Manutenção…"}
        </button>

        {showMaintenance && (
          <div className="card" style={{ marginTop: "0.5rem" }}>
            <div style={{ marginBottom: "0.6rem" }}>
              <strong style={{ fontSize: "0.9rem" }}>Aplicativos do quarto</strong>
              {apps === null ? (
                <div className="text-muted">Carregando…</div>
              ) : apps.length === 0 ? (
                <div className="text-muted">Nenhum aplicativo instalado pelo usuário.</div>
              ) : (
                <ul style={{ listStyle: "none", padding: 0, margin: "0.4rem 0 0" }}>
                  {apps.map((pkg) => (
                    <li
                      key={pkg}
                      style={{
                        display: "flex",
                        gap: "0.4rem",
                        alignItems: "center",
                        flexWrap: "wrap",
                        marginBottom: "0.3rem",
                      }}
                    >
                      <code style={{ flex: "1 1 auto" }}>{pkg}</code>
                      <button
                        className="btn btn-outline btn-sm"
                        disabled={busy}
                        onClick={() => run(() => api.launchTvApp(controllerId, pkg), `${pkg} aberto na TV.`)}
                      >
                        Abrir
                      </button>
                      <button
                        className="btn btn-outline btn-sm"
                        disabled={busy}
                        onClick={() => run(() => api.stopTvApp(controllerId, pkg), `${pkg} encerrado.`)}
                      >
                        Fechar
                      </button>
                    </li>
                  ))}
                </ul>
              )}
            </div>

            {isAdmin && (
              <div className="btn-group" style={{ flexWrap: "wrap" }}>
                <button
                  className="btn btn-danger-outline btn-sm"
                  disabled={busy}
                  onClick={async () => {
                    if (!(await confirm({
                      title: `Reiniciar a TV do ${bedName}?`,
                      text: "O quarto fica sem imagem por cerca de 40 segundos.",
                      confirmLabel: "Reiniciar",
                      danger: true,
                    })))
                      return;
                    void run(() => api.rebootTv(controllerId), "Reiniciando — a TV volta em ~40s.");
                  }}
                >
                  Reiniciar a TV
                </button>
              </div>
            )}
          </div>
        )}
      </div>
    </Modal>
  );
}
