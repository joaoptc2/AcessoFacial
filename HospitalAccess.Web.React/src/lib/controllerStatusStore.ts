import { useEffect, useState } from "react";
import { api, ApiError, type DashboardDto } from "./api";

/**
 * Polling COMPARTILHADO do /api/controllers/status: um único setInterval por aba, independente
 * de quantos componentes consomem. Antes, StatusBanner (global) e HomePage mantinham cada um o
 * seu setInterval de 30s no mesmo endpoint — 2 requisições a cada 30s na Home, dobrando a carga
 * do endpoint mais consultado do sistema.
 *
 * O último snapshot bom fica em cache (render imediato ao montar); erro transitório não apaga o
 * snapshot — o consumidor decide o que fazer com { dashboard, error }.
 */

const POLL_INTERVAL_MS = 30_000;
/** Ao ganhar um consumidor, só rebusca se o snapshot estiver mais velho que isto. */
const STALE_AFTER_MS = 10_000;

type Snapshot = { dashboard: DashboardDto | null; error: string | null };

let snapshot: Snapshot = { dashboard: null, error: null };
let lastFetchAt = 0;
const listeners = new Set<(s: Snapshot) => void>();
let timer: number | null = null;

async function fetchStatus(): Promise<void> {
  lastFetchAt = Date.now();
  try {
    snapshot = { dashboard: await api.getControllerStatus(), error: null };
  } catch (err) {
    snapshot = {
      dashboard: snapshot.dashboard,
      error: err instanceof ApiError ? err.message : "Falha ao carregar o status.",
    };
  }
  listeners.forEach((notify) => notify(snapshot));
}

/** Rebusca imediata (ex.: depois de um comando de emergência), notificando todos os consumidores. */
export function refreshControllerStatus(): Promise<void> {
  return fetchStatus();
}

export function useControllerStatus(): Snapshot {
  const [current, setCurrent] = useState<Snapshot>(snapshot);

  useEffect(() => {
    listeners.add(setCurrent);
    if (timer === null) {
      timer = window.setInterval(() => void fetchStatus(), POLL_INTERVAL_MS);
    }
    if (Date.now() - lastFetchAt > STALE_AFTER_MS) {
      void fetchStatus();
    } else {
      setCurrent(snapshot);
    }
    return () => {
      listeners.delete(setCurrent);
      if (listeners.size === 0 && timer !== null) {
        clearInterval(timer);
        timer = null;
      }
    };
  }, []);

  return current;
}
