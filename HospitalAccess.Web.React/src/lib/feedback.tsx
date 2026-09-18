import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { Icon } from "../components/Icon";

/* ============================================================
   Retorno de comando, num lugar só.

   Antes cada página inventava o seu: uma guardava `notice` e pintava um
   .alert no topo, outra empurrava a mensagem para dentro de um card, e a
   confirmação de ações destrutivas era `window.confirm` — que trava a aba,
   ignora o visual do sistema e só aceita uma linha de texto sem formatação.

   O resultado prático era que abrir uma porta e dar alta a um paciente
   pareciam operações de dois sistemas diferentes. Aqui as duas dão o mesmo
   retorno, no mesmo canto da tela, com o mesmo tempo de leitura.
   ============================================================ */

export type ToastKind = "info" | "success" | "danger";

interface ToastItem {
  id: number;
  kind: ToastKind;
  text: string;
}

export interface ConfirmOptions {
  /** A pergunta, em uma linha. Ex.: "Abrir a porta Ala Norte?" */
  title: string;
  /** O que acontece de fato. É aqui que mora a consequência, não no título. */
  text?: string;
  confirmLabel?: string;
  cancelLabel?: string;
  /** Ação destrutiva ou irreversível: muda a cor e o ícone. */
  danger?: boolean;
}

interface FeedbackValue {
  toast: (text: string, kind?: ToastKind) => void;
  toastSuccess: (text: string) => void;
  toastError: (text: string) => void;
  confirm: (options: ConfirmOptions) => Promise<boolean>;
}

const FeedbackContext = createContext<FeedbackValue | null>(null);

/** Erro fica mais tempo: quem errou um comando precisa ler o motivo. */
const DURACAO_MS: Record<ToastKind, number> = { info: 4500, success: 4500, danger: 9000 };

export function FeedbackProvider({ children }: { children: ReactNode }) {
  const [toasts, setToasts] = useState<ToastItem[]>([]);
  const [pergunta, setPergunta] = useState<ConfirmOptions | null>(null);

  // A promessa em aberto do confirm(): guardada em ref porque resolvê-la não
  // é renderização, e prendê-la no estado causaria um ciclo a mais.
  const resolver = useRef<((ok: boolean) => void) | null>(null);
  const proximoId = useRef(1);

  const remover = useCallback((id: number) => {
    setToasts((atuais) => atuais.filter((t) => t.id !== id));
  }, []);

  const toast = useCallback(
    (text: string, kind: ToastKind = "info") => {
      const id = proximoId.current++;
      setToasts((atuais) => [...atuais, { id, kind, text }]);
      window.setTimeout(() => remover(id), DURACAO_MS[kind]);
    },
    [remover],
  );

  const confirm = useCallback((options: ConfirmOptions) => {
    setPergunta(options);
    return new Promise<boolean>((resolve) => {
      resolver.current = resolve;
    });
  }, []);

  const responder = useCallback((ok: boolean) => {
    setPergunta(null);
    resolver.current?.(ok);
    resolver.current = null;
  }, []);

  // Esc cancela. Sem isso o diálogo seria mais difícil de fechar que o
  // window.confirm que ele substitui.
  useEffect(() => {
    if (!pergunta) return;
    const aoTeclar = (e: KeyboardEvent) => {
      if (e.key === "Escape") responder(false);
    };
    window.addEventListener("keydown", aoTeclar);
    return () => window.removeEventListener("keydown", aoTeclar);
  }, [pergunta, responder]);

  const valor = useMemo<FeedbackValue>(
    () => ({
      toast,
      toastSuccess: (text: string) => toast(text, "success"),
      toastError: (text: string) => toast(text, "danger"),
      confirm,
    }),
    [toast, confirm],
  );

  return (
    <FeedbackContext.Provider value={valor}>
      {children}

      {pergunta && (
        <div className="modal-backdrop" onClick={() => responder(false)}>
          <div
            className={`modal-box confirm-box${pergunta.danger ? " is-danger" : ""}`}
            role="alertdialog"
            aria-modal="true"
            aria-label={pergunta.title}
            onClick={(e) => e.stopPropagation()}
          >
            <div className="confirm-body">
              <span className="confirm-icon">
                <Icon name={pergunta.danger ? "alert" : "info"} size={20} />
              </span>
              <div>
                <p className="confirm-title">{pergunta.title}</p>
                {pergunta.text && <p className="confirm-text">{pergunta.text}</p>}
              </div>
            </div>
            <div className="confirm-actions">
              <button className="btn btn-outline" onClick={() => responder(false)}>
                {pergunta.cancelLabel ?? "Cancelar"}
              </button>
              <button
                className={pergunta.danger ? "btn btn-danger" : "btn btn-primary"}
                autoFocus
                onClick={() => responder(true)}
              >
                {pergunta.confirmLabel ?? "Confirmar"}
              </button>
            </div>
          </div>
        </div>
      )}

      <div className="toast-host" aria-live="polite">
        {toasts.map((t) => (
          <div key={t.id} className={`toast${t.kind === "info" ? "" : ` is-${t.kind}`}`}>
            <span className="toast-icon">
              <Icon name={t.kind === "success" ? "check" : t.kind === "danger" ? "alert" : "info"} size={17} />
            </span>
            <span className="toast-text">{t.text}</span>
            <button className="toast-close" aria-label="Fechar aviso" onClick={() => remover(t.id)}>
              &times;
            </button>
          </div>
        ))}
      </div>
    </FeedbackContext.Provider>
  );
}

/**
 * Avisos e confirmações. Fora do provedor devolve um substituto silencioso em
 * vez de estourar: uma tela isolada num teste não deve quebrar por falta de
 * casco.
 */
export function useFeedback(): FeedbackValue {
  const ctx = useContext(FeedbackContext);
  if (ctx) return ctx;
  return {
    toast: () => {},
    toastSuccess: () => {},
    toastError: () => {},
    confirm: () => Promise.resolve(false),
  };
}
