// Cliente HTTP tipado para a HospitalAccess.Api. Em dev, o Vite faz proxy de /api para
// http://localhost:5080 (ver vite.config.ts); em produção, a API serve os arquivos
// estáticos deste app no mesmo host/porta, então /api já é same-origin.

export interface LoginResponse {
  token: string;
  role: string;
}

export type ControllerConnectionMode = "TcpClient" | "TcpServerClient" | "Udp";

export interface ControllerDto {
  id: string;
  name: string;
  ipAddress: string;
  port: number;
  serialNumber: string;
  supportsWaitRepeatMessage: boolean;
  timeoutMs: number;
  restartCount: number;
  isRoom: boolean;
  userCount: number;
}

export interface ControllerDetailDto {
  id: string;
  name: string;
  ipAddress: string;
  port: number;
  serialNumber: string;
  supportsWaitRepeatMessage: boolean;
  timeoutMs: number;
  restartCount: number;
  connectionMode: ControllerConnectionMode;
  lastClockSyncAtUtc: string | null;
  // A senha de comunicação NÃO é retornada pela API (segredo). Apenas indica se há uma definida.
  hasCommunicationPassword: boolean;
  // API HTTP do painel web (para ler o QRCode do aparelho). Vazio = só SDK.
  apiBaseUrl: string;
  hasApiPassword: boolean;
  // Slug do quarto no Home Assistant (ex.: "quarto_101"). Vazio = sem integração HA.
  homeAssistantRoomId: string;
  // Este controlador é um quarto/leito (aparece na Gestão de Leitos)?
  isRoom: boolean;
}

// Cadastro simplificado: só nome + IP + SN obrigatórios; o resto tem default no servidor
// (porta 8000, painel http://IP, senhas = padrão global das Configurações).
export interface CreateControllerRequest {
  name: string;
  ipAddress: string;
  serialNumber: string;
  port?: number;
  communicationPassword?: string;
  supportsWaitRepeatMessage?: boolean;
  connectionMode?: ControllerConnectionMode;
  apiBaseUrl?: string;
  apiPassword?: string;
  homeAssistantRoomId?: string;
  isRoom?: boolean;
}

export interface UpdateControllerRequest {
  name: string;
  ipAddress: string;
  port: number;
  serialNumber: string;
  // Em branco/omitido mantém a senha atual (o GET não a devolve).
  communicationPassword?: string;
  supportsWaitRepeatMessage: boolean;
  connectionMode: ControllerConnectionMode;
  timeoutMs: number;
  restartCount: number;
  apiBaseUrl?: string;
  // Em branco/omitido mantém a senha atual do painel web.
  apiPassword?: string;
  homeAssistantRoomId?: string;
  isRoom?: boolean;
  // true = limpa as senhas próprias do aparelho (volta a usar a senha padrão global).
  useDefaultPasswords?: boolean;
}

export interface SyncStatusDto {
  userId: string;
  userName: string;
  state: string;
  retryCount: number;
  lastError: string | null;
  updatedAt: string;
  conflictUserCode: number | null;
}

// ---- Visão global de sincronizações (tela Sincronizações) ----
// Enums serializados como string PascalCase (JsonStringEnumConverter global do servidor).
export type SyncPendingCategory = "Pending" | "DueNow" | "WaitingBackoff" | "Quarantined" | "Conflict";
export type SyncPendingAction = "Send" | "Remove";

export interface SyncProcessingDto {
  userId: string;
  userName: string;
  sinceUtc: string;
}

export interface SyncScanDto {
  lastScanAtUtc: string;
  nextScanAtUtc: string;
  scanIntervalSeconds: number;
  lastEnqueued: number;
  pending: number;
  dueNow: number;
  waitingBackoff: number;
  quarantined: number;
  nextRetryAtUtc: string | null;
}

export interface SyncTotalsDto {
  pending: number;
  dueNow: number;
  waitingBackoff: number;
  quarantined: number;
  conflicts: number;
}

export interface SyncPendingItemDto {
  userId: string;
  userName: string;
  userType: "Permanent" | "Visitor";
  userRevoked: boolean;
  controllerId: string;
  controllerName: string;
  controllerOnline: boolean;
  state: string;
  category: SyncPendingCategory;
  action: SyncPendingAction;
  retryCount: number;
  lastError: string | null;
  updatedAtUtc: string;
  nextRetryAtUtc: string | null;
  conflictUserCode: number | null;
  inQueue: boolean;
  processingSinceUtc: string | null;
  circuitOpenUntilUtc: string | null;
}

export interface SyncOverviewDto {
  generatedAtUtc: string;
  scan: SyncScanDto | null;
  queuedUsers: number;
  processing: SyncProcessingDto[];
  totals: SyncTotalsDto;
  totalItems: number;
  items: SyncPendingItemDto[];
}

export interface DiscoveredController {
  serialNumber: string;
  ipAddress: string;
  // MAC reportado na varredura. Nos 8190H é ALEATÓRIO (muda a cada reinício) — serve para
  // conferência pontual, não para reserva DHCP.
  mac: string;
}

export interface RelocateResult {
  moved: boolean;
  oldIp?: string;
  ipAddress: string;
  message: string;
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

// Usuário "Faltando no dispositivo" — enriquecido para oferecer o reenvio individual.
export interface AuditMissingUser {
  userId: string;
  userCode: number;
  name: string;
  type: string; // "Permanent" | "Visitor"
}

export interface PersonnelAudit {
  missingOnDevice: AuditMissingUser[];
  extraOnDevice: number[];
  deviceCount: number;
  expectedCount: number;
}

// Auditoria de todos os controladores: falha de um aparelho vem em `error` (demais campos null).
export interface PersonnelAuditAllItem {
  controllerId: string;
  controllerName: string;
  error: string | null;
  missingOnDevice: AuditMissingUser[] | null;
  extraOnDevice: number[] | null;
  deviceCount: number | null;
  expectedCount: number | null;
}

export interface PersonnelAuditAllResult {
  generatedAtUtc: string;
  results: PersonnelAuditAllItem[];
}

// ---- Cópias de segurança ----
export interface BackupFileDto {
  fileName: string;
  sizeBytes: number;
  createdAtUtc: string;
}

export interface BackupListDto {
  directory: string;
  // false = o diretório configurado não é gravável; nenhuma cópia será gerada.
  writable: boolean;
  files: BackupFileDto[];
}

// A auditoria de todos os aparelhos roda em SEGUNDO PLANO: com controladores lentos a varredura
// passa de 20 min e era cortada pelo proxy reverso. A tela dispara (POST) e acompanha (GET).
export type PersonnelAuditPhase = "Idle" | "Running" | "Completed" | "Failed";

export interface PersonnelAuditSnapshot {
  phase: PersonnelAuditPhase;
  startedAtUtc: string | null;
  completedAtUtc: string | null;
  done: number;
  total: number;
  error: string | null;
  // Durante "Running" traz a auditoria ANTERIOR, para a tela não piscar vazia.
  result: PersonnelAuditAllResult | null;
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
  jobTitle: string | null;
  hasFacePhoto: boolean;
  createdByUsername: string | null;
  createdAtUtc: string;
  revokedAtUtc: string | null;
  controllers: UserControllerRef[];
}

export interface UserListPage {
  total: number;
  page: number;
  pageSize: number;
  items: UserListItemDto[];
}

/** Campos de perfil opcionais compartilhados por criação/edição. */
export interface UserProfileFields {
  document?: string | null;
  employeeId?: string | null;
  jobTitle?: string | null;
  phone?: string | null;
  email?: string | null;
  notes?: string | null;
}

export interface UserDetailControllerRef extends UserControllerRef {
  grantedByGroup: boolean;
}

export interface UserDetailDto extends UserProfileFields {
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
  revokedByUsername: string | null;
  revokedAtUtc: string | null;
  controllerIds: string[];
  controllers: UserDetailControllerRef[];
}

export interface UserAccessDoorSummary {
  controllerId: string;
  controllerName: string;
  total: number;
  granted: number;
  lastUtc: string;
}

export interface UserAccessEvent {
  timestampUtc: string;
  controllerName: string;
  method: string;
  direction: number | null;
  granted: boolean;
}

export interface UserAccessReport {
  userCode: number;
  total: number;
  granted: number;
  denied: number;
  distinctDoors: number;
  lastAccessUtc: string | null;
  doors: UserAccessDoorSummary[];
  items: UserAccessEvent[];
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
  controllers: UserControllerRef[];
  // Resumo de sincronização nas portas (se SyncSynced = 0, o QR ainda não abre nada).
  syncSynced: number;
  syncPending: number;
  syncTotal: number;
}

export interface CreateVisitorRequest {
  name: string;
  validUntil: string;
  timeGroup: number;
  controllerIds?: string[];
}

export interface ControllerStatusItem {
  id: string;
  name: string;
  ipAddress: string;
  lastSeenUtc: string | null;
  lastReachError: string | null;
  online: boolean;
  pendingSync: number;
  /** Subconjunto de pendingSync em QUARENTENA (erro permanente): o retry automático não resolve — precisa de ação manual. */
  awaitingManualSync: number;
  activeAlarms: number;
  /** Disjuntor de comandos aberto até este horário (UTC): rede OK, mas o protocolo não responde — comandos em espera para proteger o aparelho. null = normal. */
  circuitOpenUntilUtc: string | null;
}

export interface DashboardDto {
  generatedAtUtc: string;
  total: number;
  online: number;
  offline: number;
  // Não-vazio = o binário subiu na frente do banco (migration não aplicada — docs §13).
  pendingMigrations: string[];
  controllers: ControllerStatusItem[];
}

export interface EmergencyDeviceResult {
  controllerId: string;
  controllerName: string;
  success: boolean;
  error: string | null;
}

export interface EmergencyResultDto {
  action: string;
  total: number;
  succeeded: number;
  failed: number;
  devices: EmergencyDeviceResult[];
}

export type QrFormat = "Appendix8Rc4" | "PlainText";

export interface SystemSettingsDto {
  eventPhotoRetentionDays: number;
  accessLogRetentionDays: number;
  alarmLogRetentionDays: number;
  controllerAuditRetentionDays: number;
  qrFormat: QrFormat;
  // Segredos nunca são devolvidos — só o indicador de presença.
  hasDeviceDefaultCommunicationPassword: boolean;
  hasDeviceDefaultApiPassword: boolean;
  homeAssistantEnabled: boolean | null;
  homeAssistantBaseUrl: string;
  hasHomeAssistantToken: boolean;
  homeAssistantWelcomeService: string;
  homeAssistantClearService: string;
  welcomeBaseImagePath: string;
  welcomePublicBaseUrl: string;
  welcomeTextY: number | null;
  welcomeFontSize: number | null;
  welcomeFontColorHex: string;
  // Fonte enviada pela tela (vazio = fonte do sistema).
  welcomeFontPath: string;
  homeAssistantEffective: {
    enabled: boolean;
    baseUrl: string;
    hasToken: boolean;
    welcomeService: string;
    clearService: string;
  };
  updatedAtUtc: string;
  updatedByUsername: string | null;
}

// Semântica dos campos de runtime: omitido = MANTER o valor atual; string vazia = voltar a
// HERDAR o appsettings. Segredos: valor = trocar; clear* = apagar; omitido = manter.
export interface UpdateSettingsRequest {
  eventPhotoRetentionDays: number;
  accessLogRetentionDays: number;
  alarmLogRetentionDays: number;
  controllerAuditRetentionDays: number;
  qrFormat: QrFormat;
  deviceDefaultCommunicationPassword?: string;
  clearDeviceDefaultCommunicationPassword?: boolean;
  deviceDefaultApiPassword?: string;
  clearDeviceDefaultApiPassword?: boolean;
  homeAssistantEnabled?: "on" | "off" | "";
  homeAssistantBaseUrl?: string;
  homeAssistantToken?: string;
  clearHomeAssistantToken?: boolean;
  homeAssistantWelcomeService?: string;
  homeAssistantClearService?: string;
  welcomeBaseImagePath?: string;
  welcomePublicBaseUrl?: string;
  welcomeTextY?: string;
  welcomeFontSize?: string;
  welcomeFontColorHex?: string;
  // "" = voltar à fonte do sistema; omitido = manter.
  welcomeFontPath?: string;
}

// ---- Usuários do sistema (logins) ----

export type StaffRole = "Admin" | "Operator" | "Reception";

export interface StaffUserDto {
  id: string;
  username: string;
  role: StaffRole;
  active: boolean;
}

// ---- Gestão de leitos ----

export interface BedDto {
  controllerId: string;
  name: string;
  ipAddress: string;
  homeAssistantRoomId: string;
  occupied: boolean;
  stayId: string | null;
  patientName: string | null;
  startedAtUtc: string | null;
  visitorUserId: string | null;
  /** Estado da sincronização do acesso do paciente NA porta do leito (Pending/Synced/Failed/Revoked). */
  accessSyncState: string | null;
}

export interface BedHistoryItem {
  id: string;
  controllerId: string;
  controllerName: string;
  patientName: string;
  startedAtUtc: string;
  endedAtUtc: string;
  endReason: "Transfer" | "Discharge" | string;
  createdByUsername: string | null;
  endedByUsername: string | null;
}

export interface BedHistoryPage {
  total: number;
  page: number;
  pageSize: number;
  items: BedHistoryItem[];
}

export interface BedActionResult {
  stayId?: string;
  visitorUserId?: string;
  userCode?: number;
  welcomeImageUrl: string | null;
  homeAssistantCalled: boolean;
}

// ---- Logs de desenvolvimento (Admin) ----

export interface DevLogEntry {
  id: number;
  timestampUtc: string;
  level: "Trace" | "Debug" | "Information" | "Warning" | "Error" | "Critical" | string;
  category: string;
  message: string;
  exception: string | null;
}

export interface DevLogsResponse {
  enabled: boolean;
  capacity: number;
  count: number;
  entries: DevLogEntry[];
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

/**
 * Mensagem legível a partir de uma resposta com falha. Evita despejar HTML cru na tela — o
 * "413 Request Entity Too Large" do Nginx, por exemplo, vem como uma página HTML inteira.
 */
function friendlyError(status: number, body: string): string {
  if (status === 413) {
    return "A imagem enviada é muito grande. Reduza a resolução da foto e tente novamente.";
  }
  const trimmed = body.trim();
  // Corpo vazio ou HTML (típico de erro de proxy/gateway): mostra só o status, não o HTML.
  if (!trimmed || trimmed.startsWith("<")) return `Erro ${status}.`;
  return trimmed;
}

function getToken(): string | null {
  return localStorage.getItem("token");
}

function authHeaders(): HeadersInit {
  const token = getToken();
  return token ? { Authorization: `Bearer ${token}` } : {};
}

// Sessão expirada / token inválido: limpa o estado e manda para o login. Sem isto, um JWT
// vencido (expira em ~60min) deixava o app "logado" fazendo chamadas que falhavam com 401 sem
// nunca redirecionar. Dispara um evento para o AuthProvider reagir sem recarregar a página.
function handleUnauthorized() {
  const wasAuthenticated = localStorage.getItem("token") !== null;
  localStorage.removeItem("token");
  localStorage.removeItem("role");
  localStorage.removeItem("username");
  if (wasAuthenticated) {
    window.dispatchEvent(new Event("auth:unauthorized"));
  }
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
    if (response.status === 401) handleUnauthorized();
    const text = await response.text();
    throw new ApiError(response.status, friendlyError(response.status, text));
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
  getControllerStatus: () => request<DashboardDto>("/controllers/status"),
  getController: (id: string) => request<ControllerDetailDto>(`/controllers/${id}`),
  createController: (body: CreateControllerRequest) =>
    request<{ id: string }>("/controllers", { method: "POST", body: JSON.stringify(body) }),
  updateController: (id: string, body: UpdateControllerRequest) =>
    request<void>(`/controllers/${id}`, { method: "PUT", body: JSON.stringify(body) }),
  deleteController: (id: string) => request<void>(`/controllers/${id}`, { method: "DELETE" }),

  // Sem udpPort: o servidor varre as portas padrão (8101 de fábrica + 60000 legado) e mescla.
  discoverControllers: (udpPort?: number, scanSeconds?: number) =>
    request<DiscoveredController[]>(`/controllers/discover${buildQuery({ udpPort, scanSeconds })}`, {
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

  // Resincronização forçada (limpa o dispositivo e reenvia os cadastros do sistema).
  resyncAllController: (id: string) => request<{ message: string }>(`/controllers/${id}/resync-all`, { method: "POST" }),
  // Dispara o alarme de incêndio neste controlador.
  triggerFireAlarm: (id: string) => request<void>(`/controllers/${id}/alarm-fire`, { method: "POST" }),
  // Exclui uma pessoa específica (por código) do controlador (auditoria / conflito).
  deletePersonFromDevice: (id: string, userCode: number) =>
    request<void>(`/controllers/${id}/persons/${userCode}`, { method: "DELETE" }),
  // Resolução de conflito de face duplicada:
  resolveConflictReplace: (userId: string, controllerId: string) =>
    request<{ message: string }>(`/users/${userId}/sync/${controllerId}/replace`, { method: "POST" }),
  resolveConflictKeepExisting: (userId: string, controllerId: string) =>
    request<void>(`/users/${userId}/sync/${controllerId}/keep-existing`, { method: "POST" }),

  // ---- Sincronizações (visão global) ----
  getSyncOverview: () => request<SyncOverviewDto>("/sync/pending"),

  // ---- Rede ----
  getNetwork: (id: string) => request<ControllerNetworkInfo>(`/controllers/${id}/network`),
  updateNetwork: (id: string, body: ControllerNetworkInfo) =>
    request<void>(`/controllers/${id}/network`, { method: "PUT", body: JSON.stringify(body) }),
  relocateController: (id: string) =>
    request<RelocateResult>(`/controllers/${id}/relocate`, { method: "POST" }),

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
  // ---- Cópias de segurança (só Admin) ----
  getBackups: () => request<BackupListDto>("/backups"),
  createBackup: () => request<BackupFileDto>("/backups", { method: "POST" }),
  downloadBackup: (fileName: string) =>
    requestBlob(`/backups/${encodeURIComponent(fileName)}/download`),
  deleteBackup: (fileName: string) =>
    request<void>(`/backups/${encodeURIComponent(fileName)}`, { method: "DELETE" }),

  startPersonnelAuditAll: () => request<void>("/controllers/personnel-audit-all", { method: "POST" }),
  getPersonnelAuditAll: () => request<PersonnelAuditSnapshot>("/controllers/personnel-audit-all"),
  repairPersonnelAudit: (id: string) =>
    request<{ repaired: number; enqueued: number }>(`/controllers/${id}/personnel-audit/repair`, { method: "POST" }),
  repairPersonnelAuditUser: (id: string, userId: string) =>
    request<{ message: string }>(`/controllers/${id}/personnel-audit/repair/${userId}`, { method: "POST" }),

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
  getUsers: (params: { page: number; pageSize: number; search?: string; groupId?: string }) =>
    request<UserListPage>(`/users${buildQuery(params)}`),
  getUser: (id: string) => request<UserDetailDto>(`/users/${id}`),
  createUser: (form: FormData) => request<{ id: string; userCode: number }>("/users", { method: "POST", body: form }),
  updateUser: (id: string, form: FormData) => request<void>(`/users/${id}`, { method: "PUT", body: form }),
  deleteUser: (id: string) => request<void>(`/users/${id}`, { method: "DELETE" }),
  revokeUser: (id: string) => request<void>(`/users/${id}/revoke`, { method: "POST" }),
  reactivateUser: (id: string) => request<void>(`/users/${id}/reactivate`, { method: "POST" }),
  syncFailedUsers: () => request<{ enqueued: number }>("/users/sync-failed", { method: "POST" }),
  resyncUser: (id: string) => request<{ message: string }>(`/users/${id}/resync`, { method: "POST" }),
  getUserAuditLog: (id: string) => request<UserAuditLogEntry[]>(`/users/${id}/audit-log`),
  getUserPhoto: (id: string, bust?: number) => requestBlob(`/users/${id}/photo${bust ? `?v=${bust}` : ""}`),
  getUserAccessLog: (id: string, params: { from?: string; to?: string; take?: number } = {}) =>
    request<UserAccessReport>(`/users/${id}/access-log${buildQuery(params)}`),

  // ---- Visitantes ----
  getVisitors: () => request<VisitorListItemDto[]>("/visitors"),
  createVisitor: (body: CreateVisitorRequest) =>
    request<{ id: string; userCode: number }>("/visitors", { method: "POST", body: JSON.stringify(body) }),
  generateVisitorQr: (id: string) => requestBlob(`/visitors/${id}/qrcode`, { method: "POST" }),
  revokeVisitor: (id: string) => request<void>(`/visitors/${id}`, { method: "DELETE" }),
  deleteVisitor: (id: string) => request<void>(`/visitors/${id}/permanent`, { method: "DELETE" }),
  changeVisitorRoom: (id: string, controllerId: string) =>
    request<{ id: string; userCode: number; controllerId: string }>(`/visitors/${id}/room`, {
      method: "PUT",
      body: JSON.stringify({ controllerId }),
    }),

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

  // ---- Emergência (Admin) ----
  activateEmergency: () => request<EmergencyResultDto>("/emergency/activate", { method: "POST" }),
  deactivateEmergency: () => request<EmergencyResultDto>("/emergency/deactivate", { method: "POST" }),
  fireAlarmAll: () => request<EmergencyResultDto>("/emergency/fire-alarm", { method: "POST" }),
  clearAlarmsAll: () => request<EmergencyResultDto>("/emergency/clear-alarms", { method: "POST" }),

  // ---- Configurações do sistema (Admin) ----
  getSettings: () => request<SystemSettingsDto>("/settings"),
  updateSettings: (body: UpdateSettingsRequest) => request<void>("/settings", { method: "PUT", body: JSON.stringify(body) }),
  testHomeAssistant: () => request<{ ok: boolean; message: string }>("/settings/homeassistant/test", { method: "POST" }),
  uploadWelcomeImage: (file: File) => {
    const form = new FormData();
    form.append("file", file);
    return request<{ path: string; width: number; height: number }>("/settings/welcome-image", {
      method: "POST",
      body: form,
    });
  },
  previewWelcomeImage: (name: string) =>
    requestBlob(`/settings/welcome-image/preview?name=${encodeURIComponent(name)}`),
  uploadWelcomeFont: (file: File) => {
    const form = new FormData();
    form.append("file", file);
    return request<{ path: string; familyName: string }>("/settings/welcome-font", {
      method: "POST",
      body: form,
    });
  },

  // ---- Usuários do sistema (Admin) ----
  getStaffUsers: () => request<StaffUserDto[]>("/staffusers"),
  createStaffUser: (body: { username: string; password: string; role: StaffRole }) =>
    request<StaffUserDto>("/staffusers", { method: "POST", body: JSON.stringify(body) }),
  updateStaffUser: (id: string, body: { role: StaffRole; active: boolean }) =>
    request<void>(`/staffusers/${id}`, { method: "PUT", body: JSON.stringify(body) }),
  resetStaffPassword: (id: string, newPassword: string) =>
    request<void>(`/staffusers/${id}/reset-password`, { method: "POST", body: JSON.stringify({ newPassword }) }),

  // ---- Gestão de leitos ----
  getBeds: () => request<BedDto[]>("/beds"),
  getBedHistory: (params: { controllerId?: string; page: number; pageSize: number }) =>
    request<BedHistoryPage>(`/beds/history${buildQuery(params)}`),
  admitPatient: (controllerId: string, body: { patientName: string; validUntil?: string }) =>
    request<BedActionResult>(`/beds/${controllerId}/admit`, { method: "POST", body: JSON.stringify(body) }),
  transferPatient: (controllerId: string, toControllerId: string) =>
    request<BedActionResult>(`/beds/${controllerId}/transfer`, { method: "POST", body: JSON.stringify({ toControllerId }) }),
  dischargePatient: (controllerId: string) =>
    request<void>(`/beds/${controllerId}/discharge`, { method: "POST" }),
  replayWelcome: (controllerId: string) =>
    request<BedActionResult>(`/beds/${controllerId}/replay-welcome`, { method: "POST" }),

  // ---- Logs de desenvolvimento (Admin) ----
  getDevLogs: (params: { sinceId?: number; take?: number } = {}) =>
    request<DevLogsResponse>(`/devlogs${buildQuery(params)}`),
  setDevMode: (enabled: boolean) =>
    request<{ enabled: boolean }>("/devlogs/mode", { method: "POST", body: JSON.stringify({ enabled }) }),
  clearDevLogs: () => request<void>("/devlogs", { method: "DELETE" }),
};
