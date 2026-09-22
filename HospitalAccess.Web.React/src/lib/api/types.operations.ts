// Tipos de OPERAÇÃO: cópias de segurança, feriados, grade horária, painel, emergência, configurações, leitos, logs de acesso/alarme e logs de desenvolvimento.
// Parte do cliente HTTP da HospitalAccess.Api. O barril fica em ../api.ts — os imports
// da aplicação continuam sendo `from "../lib/api"`.

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
  bedStayHistoryRetentionDays: number;
  backupIntervalHours: number;
  backupMaxFiles: number;
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
  bedStayHistoryRetentionDays: number;
  backupIntervalHours: number;
  backupMaxFiles: number;
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

// ---- Gestão de leitos ----

export interface BedDto {
  controllerId: string;
  name: string;
  ipAddress: string;
  homeAssistantRoomId: string;
  /** IP do stick de TV do quarto. Vazio = leito sem TV; o botão de TV não aparece. */
  tvIpAddress: string;
  /** Última vez que o stick respondeu. Null = nunca visto. */
  tvLastSeenUtc: string | null;
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

/** Estado do stick de TV de um quarto. Model/uptime/focus só vêm quando `online`. */
export interface TvStatusDto {
  online: boolean;
  /** Online | Offline | Unauthorized | AdbMissing — a causa determina a correção. */
  state: string;
  /** Texto pronto para o operador, já explicando o que fazer. */
  message: string;
  model: string | null;
  uptimeSeconds: number | null;
  /** "pacote/atividade" em primeiro plano, ou null com a tela apagada. */
  focus: string | null;
  /** Saída literal do adb quando algo falha — é ela que aponta a correção certa. */
  detail: string | null;
}

/** Uma janela de horário: dia da semana (0 = segunda … 6 = domingo) e as duas horas em HH:MM. */
export interface ScheduleWindowDto {
  weekday: number;
  begin: string;
  end: string;
}

/** Horário de um usuário numa porta. O número da grade é detalhe do aparelho. */
export interface DoorScheduleDto {
  controllerId: string;
  controllerName: string;
  timeGroup: number;
  /** Sem restrição: o dia inteiro, todos os dias. É o padrão de quem não definiu horário. */
  unrestricted: boolean;
  label: string;
  windows: ScheduleWindowDto[];
}
