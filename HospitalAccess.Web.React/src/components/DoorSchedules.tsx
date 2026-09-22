import { useCallback, useEffect, useState } from "react";
import { api, ApiError, type DoorScheduleDto, type ScheduleWindowDto } from "../lib/api";
import { useFeedback } from "../lib/feedback";
import { Icon } from "./Icon";

const DIAS = ["Segunda", "Terça", "Quarta", "Quinta", "Sexta", "Sábado", "Domingo"];

/** Turno diurno de segunda a sexta — o preenchimento que um hospital usa na maioria das vezes. */
const ATALHO_COMERCIAL: ScheduleWindowDto[] = [0, 1, 2, 3, 4].map((d) => ({
  weekday: d,
  begin: "07:00",
  end: "19:00",
}));

function agruparPorDia(windows: ScheduleWindowDto[]): ScheduleWindowDto[][] {
  const porDia: ScheduleWindowDto[][] = [[], [], [], [], [], [], []];
  for (const w of windows) {
    if (w.weekday >= 0 && w.weekday <= 6) porDia[w.weekday].push(w);
  }
  return porDia;
}

/**
 * Horário de um usuário porta a porta.
 *
 * O aparelho guarda 64 grades e o sistema as aloca sozinho: quem opera informa as janelas, nunca
 * o número da grade. Grades de conteúdo igual são reaproveitadas, então cem pessoas no mesmo
 * turno gastam uma posição, não cem.
 *
 * Uma porta sem horário definido fica "sem restrição" — o dia inteiro, todos os dias. É o padrão
 * de todo usuário novo, e o que garante que ninguém fique trancado do lado de fora por omissão.
 */
export function DoorSchedules({ userId }: { userId: string }) {
  const { toastSuccess, toastError } = useFeedback();

  const [portas, setPortas] = useState<DoorScheduleDto[] | null>(null);
  const [erro, setErro] = useState<string | null>(null);
  const [editando, setEditando] = useState<string | null>(null);
  const [rascunho, setRascunho] = useState<ScheduleWindowDto[][]>([]);
  const [salvando, setSalvando] = useState(false);

  const carregar = useCallback(async () => {
    try {
      setPortas(await api.getUserSchedules(userId));
      setErro(null);
    } catch (e) {
      setErro(e instanceof ApiError ? e.message : "Falha ao carregar os horários.");
    }
  }, [userId]);

  useEffect(() => {
    void carregar();
  }, [carregar]);

  function abrir(porta: DoorScheduleDto) {
    setEditando(porta.controllerId);
    // "Sem restrição" abre com os dias vazios: o operador está definindo um horário, e mostrar
    // 00:00–23:59 nos sete dias só daria trabalho de apagar.
    setRascunho(porta.unrestricted ? agruparPorDia([]) : agruparPorDia(porta.windows));
  }

  function alterarJanela(dia: number, indice: number, campo: "begin" | "end", valor: string) {
    setRascunho((atual) =>
      atual.map((janelas, d) =>
        d === dia ? janelas.map((j, i) => (i === indice ? { ...j, [campo]: valor } : j)) : janelas,
      ),
    );
  }

  function adicionarJanela(dia: number) {
    setRascunho((atual) =>
      atual.map((janelas, d) =>
        d === dia && janelas.length < 8 ? [...janelas, { weekday: dia, begin: "08:00", end: "18:00" }] : janelas,
      ),
    );
  }

  function removerJanela(dia: number, indice: number) {
    setRascunho((atual) => atual.map((janelas, d) => (d === dia ? janelas.filter((_, i) => i !== indice) : janelas)));
  }

  async function salvar(controllerId: string, janelas: ScheduleWindowDto[]) {
    setSalvando(true);
    try {
      const r = await api.setUserSchedule(userId, controllerId, janelas);

      const base =
        janelas.length === 0
          ? "Porta liberada sem restrição de horário."
          : r.reused
            ? "Horário aplicado — reaproveitou uma grade que já existia nesta porta."
            : "Horário aplicado numa grade nova desta porta.";

      // O aparelho offline não invalida a gravação, mas quem operou precisa saber que ainda não
      // valeu na porta — senão testa no leitor, não funciona, e acha que o sistema errou.
      if (r.pushed) toastSuccess(base);
      else toastError(`${base} A porta não respondeu: o horário só valerá nela quando voltar.`);
      setEditando(null);
      await carregar();
    } catch (e) {
      toastError(e instanceof ApiError ? e.message : "Falha ao aplicar o horário.");
    } finally {
      setSalvando(false);
    }
  }

  if (erro) return <div className="alert alert-danger">{erro}</div>;
  if (portas === null) return <p className="text-muted">Carregando horários…</p>;
  if (portas.length === 0) {
    return <p className="text-muted">Este usuário ainda não tem porta liberada. Adicione portas no cadastro.</p>;
  }

  return (
    <div>
      <p className="text-muted" style={{ marginTop: 0, fontSize: "0.86rem" }}>
        O horário vale <strong>por porta</strong>. Sem horário definido, a porta fica liberada o tempo
        todo. O aparelho guarda 64 horários diferentes por porta e o sistema reaproveita os iguais.
      </p>

      {portas.map((porta) => (
        <div key={porta.controllerId} className="card" style={{ marginBottom: "0.6rem", padding: "0.85rem 1rem" }}>
          <div className="section-head" style={{ marginBottom: editando === porta.controllerId ? "0.75rem" : 0 }}>
            <div>
              <strong>{porta.controllerName}</strong>{" "}
              {porta.unrestricted ? (
                <span className="pill pill-success">Sem restrição</span>
              ) : (
                <span className="pill pill-warning">{porta.label}</span>
              )}
            </div>
            <div className="btn-group">
              {!porta.unrestricted && (
                <button
                  className="btn btn-outline btn-sm"
                  disabled={salvando}
                  onClick={() => salvar(porta.controllerId, [])}
                >
                  Liberar
                </button>
              )}
              <button
                className="btn btn-outline btn-sm"
                onClick={() => (editando === porta.controllerId ? setEditando(null) : abrir(porta))}
              >
                {editando === porta.controllerId ? "Cancelar" : porta.unrestricted ? "Definir horário" : "Alterar"}
              </button>
            </div>
          </div>

          {editando === porta.controllerId && (
            <div>
              <div className="btn-group" style={{ marginBottom: "0.6rem", flexWrap: "wrap" }}>
                <button
                  className="btn btn-outline btn-sm"
                  onClick={() => setRascunho(agruparPorDia(ATALHO_COMERCIAL))}
                >
                  Seg–Sex, 07:00–19:00
                </button>
                <button className="btn btn-outline btn-sm" onClick={() => setRascunho(agruparPorDia([]))}>
                  Limpar tudo
                </button>
              </div>

              {DIAS.map((nome, dia) => (
                <div
                  key={dia}
                  style={{
                    display: "flex",
                    alignItems: "center",
                    gap: "0.5rem",
                    flexWrap: "wrap",
                    padding: "0.3rem 0",
                    borderBottom: dia < 6 ? "1px solid var(--border-color)" : undefined,
                  }}
                >
                  <span style={{ width: "5.5rem", fontSize: "0.88rem", fontWeight: 500 }}>{nome}</span>

                  {rascunho[dia].length === 0 ? (
                    <span className="text-muted" style={{ fontSize: "0.82rem", flex: 1 }}>
                      fechado o dia todo
                    </span>
                  ) : (
                    <div style={{ display: "flex", gap: "0.4rem", flexWrap: "wrap", flex: 1 }}>
                      {rascunho[dia].map((janela, i) => (
                        <span key={i} style={{ display: "inline-flex", alignItems: "center", gap: "0.25rem" }}>
                          <input
                            type="time"
                            value={janela.begin}
                            onChange={(e) => alterarJanela(dia, i, "begin", e.target.value)}
                          />
                          <span className="text-muted">até</span>
                          <input
                            type="time"
                            value={janela.end}
                            onChange={(e) => alterarJanela(dia, i, "end", e.target.value)}
                          />
                          <button
                            className="btn btn-outline btn-sm"
                            title="Remover esta janela"
                            onClick={() => removerJanela(dia, i)}
                          >
                            <Icon name="close" size={13} />
                          </button>
                        </span>
                      ))}
                    </div>
                  )}

                  <button
                    className="btn btn-outline btn-sm"
                    disabled={rascunho[dia].length >= 8}
                    title={rascunho[dia].length >= 8 ? "O aparelho aceita 8 janelas por dia" : "Adicionar janela"}
                    onClick={() => adicionarJanela(dia)}
                  >
                    + janela
                  </button>
                </div>
              ))}

              <div className="btn-group" style={{ marginTop: "0.8rem" }}>
                <button
                  className={`btn btn-primary btn-sm${salvando ? " is-busy" : ""}`}
                  disabled={salvando}
                  onClick={() => salvar(porta.controllerId, rascunho.flat())}
                >
                  Aplicar horário
                </button>
                <span className="text-muted" style={{ fontSize: "0.8rem" }}>
                  Sem nenhuma janela, a porta fica liberada o tempo todo.
                </span>
              </div>
            </div>
          )}
        </div>
      ))}
    </div>
  );
}
