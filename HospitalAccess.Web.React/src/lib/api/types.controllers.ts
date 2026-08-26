// Tipos do domínio de CONTROLADORES: cadastro, status, rede, relógio, alarmes, quiosque, auditoria de pessoal, fotos de evento e a visão de sincronizações.
// Parte do cliente HTTP da HospitalAccess.Api. O barril fica em ../api.ts — os imports
// da aplicação continuam sendo `from "../lib/api"`.

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

export interface EventPhotoListItem {
  id: string;
  userCode: number | null;
  capturedAtUtc: string;
  rawEventCode: number;
  downloadedAtUtc: string;
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
