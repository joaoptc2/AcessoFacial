import { useEffect, useState } from "react";
import { api, ApiError, downloadBlob, type DeviceTraceSummary } from "../lib/api";
import { Icon } from "./Icon";
import { useFeedback } from "../lib/feedback";

const REFRESH_MS = 15000;

function duracao(ms: number): string {
  if (ms < 1000) return `${ms} ms`;
  if (ms < 60000) return `${(ms / 1000).toFixed(1)} s`;
  return `${Math.round(ms / 60000)} min`;
}

/**
 * Diagnóstico de desempenho dos aparelhos: liga a coleta, acompanha o que já foi medido e
 * exporta o CSV para análise fora do sistema.
 *
 * O painel existe porque uma coleta de vários dias precisa ser VERIFICÁVEL enquanto roda —
 * ligar uma chave e voltar três dias depois para descobrir que nada foi gravado é o pior
 * desfecho possível. Por isso o resumo mostra contagem, período coberto e amostras perdidas.
 */
export function DeviceTracePanel() {
  const { confirm, toastSuccess } = useFeedback();
  const [summary, setSummary] = useState<DeviceTraceSummary | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  async function load() {
    try {
      setSummary(await api.getDeviceTraceSummary());
      setError(null);
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Falha ao ler o diagnóstico de desempenho.");
    }
  }

  useEffect(() => {
    load();
    const timer = setInterval(load, REFRESH_MS);
    return () => clearInterval(timer);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  async function toggle() {
    if (!summary) return;
    setBusy(true);
    try {
      const result = await api.setDeviceTraceMode(!summary.enabled);
      toastSuccess(
        result.enabled
          ? "Coleta LIGADA — cada comando aos aparelhos passa a ser medido."
          : "Coleta desligada. O que já foi medido continua disponível para exportar.",
      );
      await load();
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Falha ao alterar a coleta.");
    } finally {
      setBusy(false);
    }
  }

  async function exportar() {
    setBusy(true);
    try {
      const blob = await api.downloadDeviceTraceCsv();
      downloadBlob(blob, `desempenho-aparelhos-${new Date().toISOString().slice(0, 10)}.csv`);
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Falha ao exportar o CSV.");
    } finally {
      setBusy(false);
    }
  }

  async function limpar() {
    if (!(await confirm({
      title: "Descartar as medições coletadas?",
      text: "Apaga tudo o que foi medido até agora para começar uma janela limpa. Exporte o CSV antes se ainda for precisar.",
      confirmLabel: "Descartar",
      danger: true,
    })))
      return;
    setBusy(true);
    try {
      const result = await api.clearDeviceTrace();
      toastSuccess(`${result.removed} medições descartadas.`);
      await load();
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Falha ao descartar as medições.");
    } finally {
      setBusy(false);
    }
  }

  if (!summary) return null;

  const periodo =
    summary.firstAtUtc && summary.lastAtUtc
      ? `${new Date(summary.firstAtUtc).toLocaleString()} → ${new Date(summary.lastAtUtc).toLocaleString()}`
      : "—";

  return (
    <div className="panel">
      <div className="panel-head">
        <Icon name="device" size={18} />
        <h3>Desempenho dos aparelhos (diagnóstico)</h3>
        <span className={summary.enabled ? "pill pill-success" : "pill"} style={{ marginLeft: "auto" }}>
          {summary.enabled ? "coletando" : "desligado"}
        </span>
      </div>
      <div className="panel-body">
        {error && <div className="alert alert-danger">{error}</div>}

        <p className="text-muted" style={{ marginTop: 0 }}>
          Registra uma linha por comando enviado a um aparelho, separando <strong>espera na fila</strong> de{" "}
          <strong>tempo do aparelho</strong> — é essa separação que diz se a lentidão vem da nossa
          concorrência ou do equipamento. Ligue por alguns dias, exporte o CSV e desligue. As medições
          são apagadas sozinhas após {summary.retentionDays} dia(s).
        </p>

        <div className="stat-row">
          <div className="stat-tile">
            <span className="stat-value">{summary.total.toLocaleString()}</span>
            <span className="stat-label">Medições</span>
          </div>
          <div className="stat-tile">
            <span className="stat-value">{summary.written.toLocaleString()}</span>
            <span className="stat-label">Gravadas (desde o boot)</span>
          </div>
          <div className="stat-tile">
            <span className="stat-value" style={{ color: summary.dropped > 0 ? "var(--danger)" : undefined }}>
              {summary.dropped.toLocaleString()}
            </span>
            <span className="stat-label">Perdidas (fila cheia)</span>
          </div>
        </div>

        <p className="text-muted" style={{ fontSize: "0.85rem" }}>
          Período coberto: <strong>{periodo}</strong>
          {summary.dropped > 0 && (
            <>
              {" — "}
              <span style={{ color: "var(--danger)" }}>
                atenção: {summary.dropped.toLocaleString()} amostras foram descartadas por fila cheia,
                então o CSV tem lacunas nos picos.
              </span>
            </>
          )}
        </p>

        <div className="btn-group" style={{ flexWrap: "wrap" }}>
          <button
            className={`btn ${summary.enabled ? "btn-outline" : "btn-primary"}`}
            disabled={busy}
            onClick={toggle}
          >
            {summary.enabled ? "Parar a coleta" : "Iniciar a coleta"}
          </button>
          <button className="btn btn-outline" disabled={busy || summary.total === 0} onClick={exportar}>
            Baixar CSV
          </button>
          <button className="btn btn-danger-outline" disabled={busy || summary.total === 0} onClick={limpar}>
            Descartar medições
          </button>
        </div>

        {summary.byOperation.length > 0 && (
          <>
            <h4 style={{ marginBottom: "0.4rem" }}>Onde o tempo está sendo gasto</h4>
            <p className="text-muted" style={{ fontSize: "0.82rem", marginTop: 0 }}>
              Ordenado pelo <strong>tempo total</strong>, não pela média: uma operação de 200&nbsp;ms
              chamada 10 mil vezes pesa mais que uma de 8&nbsp;s chamada duas vezes.
            </p>
            <div className="table-wrap">
              <table>
                <thead>
                  <tr>
                    <th>Canal</th>
                    <th>Operação</th>
                    <th>Chamadas</th>
                    <th>Tempo total</th>
                    <th>Média</th>
                    <th>Espera média</th>
                    <th>Falhas</th>
                  </tr>
                </thead>
                <tbody>
                  {summary.byOperation.map((op) => (
                    <tr key={`${op.channel}-${op.operation}`}>
                      <td>{op.channel}</td>
                      <td>{op.operation}</td>
                      <td>{op.chamadas.toLocaleString()}</td>
                      <td>{duracao(op.tempoTotalMs)}</td>
                      <td>{duracao(op.mediaMs)}</td>
                      <td>{duracao(op.esperaMediaMs)}</td>
                      <td>
                        {op.falhas > 0 ? (
                          <span className="pill pill-danger">{op.falhas}</span>
                        ) : (
                          <span className="text-muted">0</span>
                        )}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </>
        )}
      </div>
    </div>
  );
}
