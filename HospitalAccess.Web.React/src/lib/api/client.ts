// Superfície de chamadas da API, agrupada por domínio.
// Parte do cliente HTTP da HospitalAccess.Api. O barril fica em ../api.ts — os imports
// da aplicação continuam sendo `from "../lib/api"`.

import { buildQuery, request, requestBlob } from "./http";
import type { AlarmSettings, ControllerDetailDto, ControllerDto, ControllerNetworkInfo, CreateControllerRequest, DiscoveredController, EventPhotoListItem, KioskSettings, PersonnelAudit, PersonnelAuditSnapshot, RelocateResult, SyncOverviewDto, SyncStatusDto, UpdateControllerRequest } from "./types.controllers";
import type { CreateVisitorRequest, LoginResponse, StaffRole, StaffUserDto, UserAccessReport, UserAuditLogEntry, UserDetailDto, UserGroupDto, UserGroupRequest, UserListPage, VisitorListItemDto } from "./types.people";
import type { AccessLogPage, AlarmEventPage, BackupFileDto, BackupListDto, BedActionResult, BedDto, BedHistoryPage, DashboardDto, DevLogsResponse, EmergencyResultDto, HolidayDto, HolidayRequest, SystemSettingsDto, TimeGroupScheduleDto, TimeGroupScheduleRequest, UpdateSettingsRequest } from "./types.operations";

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
