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

export interface DiscoveredController {
  serialNumber: string;
  ipAddress: string;
}

export interface ControllerNetworkInfo {
  mac: string;
  ip: string;
  ipMask: string;
  ipGateway: string;
  dns: string;
  dnsBackup: string;
  udpPort: number;
  serverIp: string;
  serverAddr: string;
  serverPort: number;
  autoIp: boolean;
}

export interface AlarmSettings {
  fireAlarmEnabled: boolean;
  blacklistAlarmEnabled: boolean;
  tamperAlarmEnabled: boolean;
  illegalVerificationAlarmEnabled: boolean;
  illegalVerificationTimes: number;
  duressAlarmEnabled: boolean;
  duressPassword: string | null;
  openDoorTimeoutAlarmEnabled: boolean;
  openDoorTimeoutSeconds: number;
  legalVerificationCloseAlarmEnabled: boolean;
}

export interface KioskSettings {
  language: number;
  volume: number;
  fillLightMode: number;
  maskDetectionMode: number;
  temperatureDetectionMode: number;
  temperatureAlarmThresholdX10: number;
  temperatureDisplayMode: number;
  faceIdentifyRange: number;
  livenessDetectionMode: number;
  livenessSimilarity: number;
}

export interface PersonnelAudit {
  missingOnDevice: number[];
  extraOnDevice: number[];
  deviceCount: number;
  expectedCount: number;
}

export interface EventPhotoListItem {
  id: string;
  userCode: number | null;
  capturedAtUtc: string;
  rawEventCode: number;
  downloadedAtUtc: string;
}

export interface HolidayDto {
  id: string;
  index: number;
  name: string;
  date: string;
  repeatsYearly: boolean;
  holidayType: number;
}

export interface HolidayRequest {
  index: number;
  name: string;
  date: string;
  repeatsYearly: boolean;
  holidayType: number;
}

export interface TimeGroupSegmentDto {
  weekday: number;
  segmentIndex: number;
  beginTime: string;
  endTime: string;
}

export interface TimeGroupScheduleDto {
  id: string;
  groupNumber: number;
  name: string;
  segments: TimeGroupSegmentDto[];
}

export interface TimeGroupScheduleRequest {
  groupNumber: number;
  name: string;
  segments: TimeGroupSegmentDto[];
}

export interface UserGroupDto {
  id: string;
  name: string;
  description: string | null;
  userCount: number;
  defaultControllerIds: string[];
}

export interface UserGroupRequest {
  name: string;
  description: string | null;
}

export interface UserControllerRef {
  controllerId: string;
  controllerName: string;
}

export interface UserListItemDto {
  id: string;
  userCode: number;
  name: string;
  timeGroup: number;
  groupId: string | null;
  groupName: string | null;
  cardNumber: number | null;
  hasFacePhoto: boolean;
  createdByUsername: string | null;
  createdAtUtc: string;
  revokedAtUtc: string | null;
  controllers: UserControllerRef[];
}

export interface UserDetailDto {
  id: string;
  userCode: number;
  name: string;
  timeGroup: number;
  groupId: string | null;
  cardNumber: number | null;
  hasFacePhoto: boolean;
  controllerIds: string[];
}

export interface UserAuditLogEntry {
  timestampUtc: string;
  action: string;
  performedByUsername: string | null;
  details: string | null;
}

export interface VisitorListItemDto {
  id: string;
  userCode: number;
  name: string;
  validFrom: string | null;
  validUntil: string | null;
  timeGroup: number;
  createdByUsername: string | null;
  createdAtUtc: string;
  revokedAtUtc: string | null;
  isExpired: boolean;
  isRevoked: boolean;
}

export interface CreateVisitorRequest {
  name: string;
  validUntil: string;
  timeGroup: number;
}

export interface AccessLogItem {
  id: string;
  timestampUtc: string;
  userCode: number | null;
  userName: string | null;
  controllerId: string;
  controllerName: string;
  method: string;
  rawEventCode: number;
  direction: number | null;
  granted: boolean;
}

export interface AccessLogPage {
  total: number;
  page: number;
  pageSize: number;
  items: AccessLogItem[];
}

export interface AlarmEventItem {
  id: string;
  timestampUtc: string;
  controllerId: string;
  controllerName: string;
  kind: string;
  rawEventCode: number;
  cleared: boolean;
}

export interface AlarmEventPage {
  total: number;
  page: number;
  pageSize: number;
  items: AlarmEventItem[];
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

function authHeaders(): HeadersInit {
  const token = getToken();
  return token ? { Authorization: `Bearer ${token}` } : {};
}

async function request<T>(path: string, options: RequestInit = {}): Promise<T> {
  const headers = new Headers(options.headers);
  const token = getToken();
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

async function requestBlob(path: string, options: RequestInit = {}): Promise<Blob> {
  const headers = new Headers(options.headers);
  const token = getToken();
  if (token) headers.set("Authorization", `Bearer ${token}`);
  const response = await fetch(`/api${path}`, { ...options, headers });
  if (!response.ok) {
    const text = await response.text();
    throw new ApiError(response.status, text || `Erro ${response.status}`);
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

function buildQuery(params: Record<string, string | number | boolean | undefined | null>): string {
  const parts: string[] = [];
  for (const [key, value] of Object.entries(params)) {
    if (value === undefined || value === null || value === "") continue;
    parts.push(`${key}=${encodeURIComponent(String(value))}`);
  }
  return parts.length > 0 ? `?${parts.join("&")}` : "";
}

export { ApiError, authHeaders };

export const api = {
  login: (username: string, password: string) =>
    request<LoginResponse>("/auth/login", { method: "POST", body: JSON.stringify({ username, password }) }),

  // ---- Controladores ----
  getControllers: () => request<ControllerDto[]>("/controllers"),
  getController: (id: string) => request<ControllerDetailDto>(`/controllers/${id}`),
  createController: (body: CreateControllerRequest) =>
    request<{ id: string }>("/controllers", { method: "POST", body: JSON.stringify(body) }),
  updateController: (id: string, body: UpdateControllerRequest) =>
    request<void>(`/controllers/${id}`, { method: "PUT", body: JSON.stringify(body) }),
  deleteController: (id: string) => request<void>(`/controllers/${id}`, { method: "DELETE" }),

  discoverControllers: (udpPort = 60000, scanSeconds = 4) =>
    request<DiscoveredController[]>(`/controllers/discover?udpPort=${udpPort}&scanSeconds=${scanSeconds}`, {
      method: "POST",
    }),

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

  // ---- Rede ----
  getNetwork: (id: string) => request<ControllerNetworkInfo>(`/controllers/${id}/network`),
  updateNetwork: (id: string, body: ControllerNetworkInfo) =>
    request<void>(`/controllers/${id}/network`, { method: "PUT", body: JSON.stringify(body) }),

  // ---- Relógio ----
  getClock: (id: string) => request<string>(`/controllers/${id}/clock`),
  syncClock: (id: string) => request<void>(`/controllers/${id}/clock/sync`, { method: "POST" }),

  // ---- Alarmes (config. por controlador) ----
  getAlarmSettings: (id: string) => request<AlarmSettings>(`/controllers/${id}/alarm-settings`),
  updateAlarmSettings: (id: string, body: AlarmSettings) =>
    request<void>(`/controllers/${id}/alarm-settings`, { method: "PUT", body: JSON.stringify(body) }),
  clearAlarm: (id: string) => request<void>(`/controllers/${id}/alarm-clear`, { method: "POST" }),

  // ---- Ajustes locais (quiosque) ----
  getKioskSettings: (id: string) => request<KioskSettings>(`/controllers/${id}/kiosk-settings`),
  updateKioskSettings: (id: string, body: KioskSettings) =>
    request<void>(`/controllers/${id}/kiosk-settings`, { method: "PUT", body: JSON.stringify(body) }),

  // ---- Auditoria / leitura reversa ----
  getPersonnelAudit: (id: string) => request<PersonnelAudit>(`/controllers/${id}/personnel-audit`),

  // ---- Foto do evento ----
  downloadEventPhotos: (id: string, quantity: number) =>
    request<{ downloaded: number }>(`/controllers/${id}/event-photos/download?quantity=${quantity}`, {
      method: "POST",
    }),
  getEventPhotos: (id: string) => request<EventPhotoListItem[]>(`/controllers/${id}/event-photos`),
  eventPhotoImageUrl: (photoId: string) => `/api/controllers/event-photos/${photoId}/image`,

  // ---- Grupos de usuários ----
  getUserGroups: () => request<UserGroupDto[]>("/usergroups"),
  createUserGroup: (body: UserGroupRequest) =>
    request<{ id: string }>("/usergroups", { method: "POST", body: JSON.stringify(body) }),
  updateUserGroup: (id: string, body: UserGroupRequest) =>
    request<void>(`/usergroups/${id}`, { method: "PUT", body: JSON.stringify(body) }),
  deleteUserGroup: (id: string) => request<void>(`/usergroups/${id}`, { method: "DELETE" }),
  updateGroupDefaultControllers: (id: string, controllerIds: string[]) =>
    request<void>(`/usergroups/${id}/default-controllers`, {
      method: "PUT",
      body: JSON.stringify({ controllerIds }),
    }),

  // ---- Usuários permanentes ----
  getUsers: () => request<UserListItemDto[]>("/users"),
  getUser: (id: string) => request<UserDetailDto>(`/users/${id}`),
  createUser: (form: FormData) => request<{ id: string; userCode: number }>("/users", { method: "POST", body: form }),
  updateUser: (id: string, form: FormData) => request<void>(`/users/${id}`, { method: "PUT", body: form }),
  deleteUser: (id: string) => request<void>(`/users/${id}`, { method: "DELETE" }),
  revokeUser: (id: string) => request<void>(`/users/${id}/revoke`, { method: "POST" }),
  reactivateUser: (id: string) => request<void>(`/users/${id}/reactivate`, { method: "POST" }),
  getUserAuditLog: (id: string) => request<UserAuditLogEntry[]>(`/users/${id}/audit-log`),

  // ---- Visitantes ----
  getVisitors: () => request<VisitorListItemDto[]>("/visitors"),
  createVisitor: (body: CreateVisitorRequest) =>
    request<{ id: string; userCode: number }>("/visitors", { method: "POST", body: JSON.stringify(body) }),
  generateVisitorQr: (id: string) => requestBlob(`/visitors/${id}/qrcode`, { method: "POST" }),
  revokeVisitor: (id: string) => request<void>(`/visitors/${id}`, { method: "DELETE" }),

  // ---- Feriados ----
  getHolidays: () => request<HolidayDto[]>("/holidays"),
  createHoliday: (body: HolidayRequest) => request<{ id: string }>("/holidays", { method: "POST", body: JSON.stringify(body) }),
  updateHoliday: (id: string, body: HolidayRequest) =>
    request<void>(`/holidays/${id}`, { method: "PUT", body: JSON.stringify(body) }),
  deleteHoliday: (id: string) => request<void>(`/holidays/${id}`, { method: "DELETE" }),
  syncAllHolidays: () => request<{ controllerCount: number }>("/holidays/sync-all", { method: "POST" }),

  // ---- Grade horária ----
  getTimeGroups: () => request<TimeGroupScheduleDto[]>("/timegroups"),
  createTimeGroup: (body: TimeGroupScheduleRequest) =>
    request<{ id: string }>("/timegroups", { method: "POST", body: JSON.stringify(body) }),
  updateTimeGroup: (id: string, body: TimeGroupScheduleRequest) =>
    request<void>(`/timegroups/${id}`, { method: "PUT", body: JSON.stringify(body) }),
  deleteTimeGroup: (id: string) => request<void>(`/timegroups/${id}`, { method: "DELETE" }),
  syncAllTimeGroups: () => request<{ controllerCount: number }>("/timegroups/sync-all", { method: "POST" }),

  // ---- Log de acessos ----
  queryAccessLog: (params: {
    from?: string;
    to?: string;
    userCode?: number;
    controllerId?: string;
    method?: string;
    page: number;
    pageSize: number;
  }) => request<AccessLogPage>(`/accesslog${buildQuery(params)}`),
  exportAccessLogCsv: (params: { from?: string; to?: string; userCode?: number; controllerId?: string; method?: string }) =>
    requestBlob(`/accesslog/export${buildQuery({ ...params, format: "csv" })}`),

  // ---- Log de alarmes ----
  queryAlarmEvents: (params: { from?: string; to?: string; controllerId?: string; kind?: string; page: number; pageSize: number }) =>
    request<AlarmEventPage>(`/alarmevents${buildQuery(params)}`),
  exportAlarmEventsCsv: (params: { from?: string; to?: string; controllerId?: string; kind?: string }) =>
    requestBlob(`/alarmevents/export${buildQuery(params)}`),
};
