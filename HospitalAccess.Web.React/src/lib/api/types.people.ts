// Tipos do domínio de PESSOAS: login, grupos, usuários permanentes, visitantes e usuários do sistema.
// Parte do cliente HTTP da HospitalAccess.Api. O barril fica em ../api.ts — os imports
// da aplicação continuam sendo `from "../lib/api"`.

export interface LoginResponse {
  token: string;
  role: string;
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

// ---- Usuários do sistema (logins) ----

export type StaffRole = "Admin" | "Operator" | "Reception";

export interface StaffUserDto {
  id: string;
  username: string;
  role: StaffRole;
  active: boolean;
}
