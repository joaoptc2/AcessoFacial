// Núcleo do cliente HTTP: erro tipado, cabeçalho de autenticação, sessão expirada.
// Parte do cliente HTTP da HospitalAccess.Api. O barril fica em ../api.ts — os imports
// da aplicação continuam sendo `from "../lib/api"`.

export class ApiError extends Error {
  status: number;

  constructor(status: number, message: string) {
    super(message);
    this.status = status;
  }
}

/**
 * Mensagem legível a partir de uma resposta com falha. Evita despejar HTML cru na tela — o
 * "413 Request Entity Too Large" do Nginx, por exemplo, vem como uma página HTML inteira.
 */
export function friendlyError(status: number, body: string): string {
  if (status === 413) {
    return "A imagem enviada é muito grande. Reduza a resolução da foto e tente novamente.";
  }
  const trimmed = body.trim();
  // Corpo vazio ou HTML (típico de erro de proxy/gateway): mostra só o status, não o HTML.
  if (!trimmed || trimmed.startsWith("<")) return `Erro ${status}.`;
  return trimmed;
}

export function getToken(): string | null {
  return localStorage.getItem("token");
}

export function authHeaders(): HeadersInit {
  const token = getToken();
  return token ? { Authorization: `Bearer ${token}` } : {};
}

// Sessão expirada / token inválido: limpa o estado e manda para o login. Sem isto, um JWT
// vencido (expira em ~60min) deixava o app "logado" fazendo chamadas que falhavam com 401 sem
// nunca redirecionar. Dispara um evento para o AuthProvider reagir sem recarregar a página.
export function handleUnauthorized() {
  const wasAuthenticated = localStorage.getItem("token") !== null;
  localStorage.removeItem("token");
  localStorage.removeItem("role");
  localStorage.removeItem("username");
  if (wasAuthenticated) {
    window.dispatchEvent(new Event("auth:unauthorized"));
  }
}

export async function request<T>(path: string, options: RequestInit = {}): Promise<T> {
  const headers = new Headers(options.headers);
  const token = getToken();
  if (token) headers.set("Authorization", `Bearer ${token}`);
  if (options.body && !(options.body instanceof FormData)) {
    headers.set("Content-Type", "application/json");
  }

  const response = await fetch(`/api${path}`, { ...options, headers });

  if (!response.ok) {
    if (response.status === 401) handleUnauthorized();
    const text = await response.text();
    throw new ApiError(response.status, friendlyError(response.status, text));
  }

  if (response.status === 204) return undefined as T;
  const contentType = response.headers.get("content-type") ?? "";
  if (contentType.includes("application/json")) return (await response.json()) as T;
  return undefined as T;
}

export async function requestBlob(path: string, options: RequestInit = {}): Promise<Blob> {
  const headers = new Headers(options.headers);
  const token = getToken();
  if (token) headers.set("Authorization", `Bearer ${token}`);
  const response = await fetch(`/api${path}`, { ...options, headers });
  if (!response.ok) {
    if (response.status === 401) handleUnauthorized();
    const text = await response.text();
    throw new ApiError(response.status, friendlyError(response.status, text));
  }
  return response.blob();
}

/** Dispara o download de um Blob no navegador (equivalente ao downloadFileFromBase64 do lado Blazor). */
export function downloadBlob(blob: Blob, filename: string) {
  const url = URL.createObjectURL(blob);
  const a = document.createElement("a");
  a.href = url;
  a.download = filename;
  a.click();
  URL.revokeObjectURL(url);
}

export function buildQuery(params: Record<string, string | number | boolean | undefined | null>): string {
  const parts: string[] = [];
  for (const [key, value] of Object.entries(params)) {
    if (value === undefined || value === null || value === "") continue;
    parts.push(`${key}=${encodeURIComponent(String(value))}`);
  }
  return parts.length > 0 ? `?${parts.join("&")}` : "";
}


