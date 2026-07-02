// Cliente HTTP tipado para a HospitalAccess.Api. Em dev, o Vite faz proxy de /api para
// http://localhost:5080 (ver vite.config.ts); em produção, a API serve os arquivos
// estáticos deste app no mesmo host/porta, então /api já é same-origin.

export interface LoginResponse {
  token: string;
  role: string;
}

export interface ControllerDto {
  id: string;
  name: string;
  ipAddress: string;
  port: number;
  serialNumber: string;
  supportsWaitRepeatMessage: boolean;
  relayIndex: number;
  timeoutMs: number;
  restartCount: number;
  userCount: number;
}

export interface ControllerDetailDto extends ControllerDto {
  communicationPassword: string;
  lastClockSyncAtUtc: string | null;
}

export interface CreateControllerRequest {
  name: string;
  ipAddress: string;
  port: number;
  serialNumber: string;
  communicationPassword: string;
  supportsWaitRepeatMessage: boolean;
}

export interface UpdateControllerRequest extends CreateControllerRequest {
  relayIndex: number;
  timeoutMs: number;
  restartCount: number;
}

export interface SyncStatusDto {
  userId: string;
  userName: string;
  state: string;
  retryCount: number;
  lastError: string | null;
  updatedAt: string;
}

class ApiError extends Error {
  status: number;

  constructor(status: number, message: string) {
    super(message);
    this.status = status;
  }
}

function getToken(): string | null {
  return localStorage.getItem("token");
}

async function request<T>(path: string, options: RequestInit = {}): Promise<T> {
  const token = getToken();
  const headers = new Headers(options.headers);
  if (token) headers.set("Authorization", `Bearer ${token}`);
  if (options.body && !(options.body instanceof FormData)) {
    headers.set("Content-Type", "application/json");
  }

  const response = await fetch(`/api${path}`, { ...options, headers });

  if (!response.ok) {
    const text = await response.text();
    throw new ApiError(response.status, text || `Erro ${response.status}`);
  }

  if (response.status === 204) return undefined as T;
  const contentType = response.headers.get("content-type") ?? "";
  if (contentType.includes("application/json")) return (await response.json()) as T;
  return undefined as T;
}

export { ApiError };

export const api = {
  login: (username: string, password: string) =>
    request<LoginResponse>("/auth/login", { method: "POST", body: JSON.stringify({ username, password }) }),

  getControllers: () => request<ControllerDto[]>("/controllers"),
  getController: (id: string) => request<ControllerDetailDto>(`/controllers/${id}`),
  createController: (body: CreateControllerRequest) =>
    request<{ id: string }>("/controllers", { method: "POST", body: JSON.stringify(body) }),
  updateController: (id: string, body: UpdateControllerRequest) =>
    request<void>(`/controllers/${id}`, { method: "PUT", body: JSON.stringify(body) }),
  deleteController: (id: string) => request<void>(`/controllers/${id}`, { method: "DELETE" }),

  discoverControllers: (udpPort = 60000, scanSeconds = 4) =>
    request<{ serialNumber: string; ipAddress: string }[]>(
      `/controllers/discover?udpPort=${udpPort}&scanSeconds=${scanSeconds}`,
      { method: "POST" },
    ),

  openDoor: (id: string) => request<void>(`/controllers/${id}/open`, { method: "POST" }),
  closeDoor: (id: string) => request<void>(`/controllers/${id}/close`, { method: "POST" }),
  holdDoorOpen: (id: string) => request<void>(`/controllers/${id}/hold-open`, { method: "POST" }),
  lockDoor: (id: string) => request<void>(`/controllers/${id}/lock`, { method: "POST" }),
  unlockDoor: (id: string) => request<void>(`/controllers/${id}/unlock`, { method: "POST" }),

  testConnection: (id: string) =>
    request<{ reportedSerialNumber: string; matchesRegistered: boolean }>(`/controllers/${id}/test-connection`, {
      method: "POST",
    }),
  getSyncStatus: (id: string) => request<SyncStatusDto[]>(`/controllers/${id}/sync-status`),
};
