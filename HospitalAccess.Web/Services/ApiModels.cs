namespace HospitalAccess.Web.Services;

public record LoginResponse(string Token, string Role);

// Controladores (cada um representa fisicamente uma única porta).
public record ControllerDto(
    Guid Id, string Name, string IpAddress, int Port, string SerialNumber,
    bool SupportsWaitRepeatMessage, int RelayIndex, int TimeoutMs, int RestartCount, int UserCount);

// CommunicationPassword não é mais retornada pela API (segredo). HasCommunicationPassword indica
// se há uma definida; ConnectionMode define como o servidor fala com o controlador.
public record ControllerDetailDto(
    Guid Id, string Name, string IpAddress, int Port, string SerialNumber,
    bool SupportsWaitRepeatMessage, int RelayIndex, int TimeoutMs, int RestartCount,
    string ConnectionMode, DateTime? LastClockSyncAtUtc, bool HasCommunicationPassword);

public record CreateControllerRequest(
    string Name, string IpAddress, int Port, string SerialNumber,
    string CommunicationPassword, bool SupportsWaitRepeatMessage, int RelayIndex = 0,
    string ConnectionMode = "TcpClient");

// CommunicationPassword em branco/nulo na edição mantém a atual (a API não a devolve).
public record UpdateControllerRequest(
    string Name, string IpAddress, int Port, string SerialNumber,
    string? CommunicationPassword, bool SupportsWaitRepeatMessage, int RelayIndex,
    int TimeoutMs, int RestartCount, string ConnectionMode = "TcpClient");

// Grupos organizacionais de usuários.
public record UserGroupDto(Guid Id, string Name, string? Description, int UserCount, IEnumerable<Guid> DefaultControllerIds);
public record UserGroupRequest(string Name, string? Description);
public record UpdateGroupDefaultControllersRequest(Guid[] ControllerIds);
public record CreateGroupResponse(Guid Id);

// Usuários permanentes.
public record UserControllerRef(Guid ControllerId, string ControllerName);
public record UserListItemDto(
    Guid Id, uint UserCode, string Name, int TimeGroup, Guid? GroupId, string? GroupName, uint? CardNumber,
    bool HasFacePhoto, string? CreatedByUsername, DateTime CreatedAtUtc, DateTime? RevokedAtUtc,
    IEnumerable<UserControllerRef> Controllers);

public record UserDetailDto(
    Guid Id, uint UserCode, string Name, int TimeGroup, Guid? GroupId, uint? CardNumber, bool HasFacePhoto,
    IEnumerable<Guid> ControllerIds);

public record CreateUserResponse(Guid Id, uint UserCode);
public record UserAuditLogEntry(DateTime TimestampUtc, string Action, string? PerformedByUsername, string? Details);

// Visitantes/temporários.
public record VisitorListItemDto(
    Guid Id, uint UserCode, string Name, DateTime? ValidFrom, DateTime? ValidUntil, int TimeGroup,
    string? CreatedByUsername, DateTime CreatedAtUtc, DateTime? RevokedAtUtc, bool IsExpired, bool IsRevoked);

public record CreateVisitorRequest(string Name, DateTime ValidUntil, int TimeGroup);

public record SyncStatusDto(Guid UserId, string UserName, string State, int RetryCount, string? LastError, DateTime UpdatedAt);

public record AccessLogItem(
    Guid Id, DateTime TimestampUtc, uint? UserCode, string? UserName,
    Guid ControllerId, string ControllerName,
    string Method, int RawEventCode, int? Direction, bool Granted);

public record AccessLogPage(int Total, int Page, int PageSize, List<AccessLogItem> Items);

// Rede do controlador (classe mutável — precisa ser bindável em EditForm).
public class ControllerNetworkInfo
{
    public string Mac { get; set; } = string.Empty;
    public string Ip { get; set; } = string.Empty;
    public string IpMask { get; set; } = string.Empty;
    public string IpGateway { get; set; } = string.Empty;
    public string Dns { get; set; } = string.Empty;
    public string DnsBackup { get; set; } = string.Empty;
    public int UdpPort { get; set; }
    public string ServerIp { get; set; } = string.Empty;
    public string ServerAddr { get; set; } = string.Empty;
    public int ServerPort { get; set; }
    public bool AutoIp { get; set; }
}

public record DiscoveredController(string SerialNumber, string IpAddress);

// Relógio.
public record ClockInfo(DateTime ControllerDateUtc);

// Feriados.
public record HolidayDto(Guid Id, byte Index, string Name, DateTime Date, bool RepeatsYearly, byte HolidayType);
public record HolidayRequest(byte Index, string Name, DateTime Date, bool RepeatsYearly, byte HolidayType);

// Grade horária.
public record TimeGroupSegmentDto(byte Weekday, byte SegmentIndex, TimeOnly BeginTime, TimeOnly EndTime);
public record TimeGroupScheduleDto(Guid Id, int GroupNumber, string Name, List<TimeGroupSegmentDto> Segments);
public record TimeGroupSegmentRequest(byte Weekday, byte SegmentIndex, TimeOnly BeginTime, TimeOnly EndTime);
public record TimeGroupScheduleRequest(int GroupNumber, string Name, List<TimeGroupSegmentRequest> Segments);

// Alarmes (classe mutável — precisa ser bindável em EditForm).
public class AlarmSettings
{
    public bool FireAlarmEnabled { get; set; }
    public bool BlacklistAlarmEnabled { get; set; }
    public bool TamperAlarmEnabled { get; set; }
    public bool IllegalVerificationAlarmEnabled { get; set; }
    public byte IllegalVerificationTimes { get; set; }
    public bool DuressAlarmEnabled { get; set; }
    public string? DuressPassword { get; set; }
    public bool OpenDoorTimeoutAlarmEnabled { get; set; }
    public int OpenDoorTimeoutSeconds { get; set; }
    public bool LegalVerificationCloseAlarmEnabled { get; set; }
}

public record AlarmEventItem(
    Guid Id, DateTime TimestampUtc, Guid ControllerId, string ControllerName,
    string Kind, int RawEventCode, bool Cleared);

public record AlarmEventPage(int Total, int Page, int PageSize, List<AlarmEventItem> Items);

// Ajustes locais do quiosque (classe mutável — precisa ser bindável em EditForm).
public class KioskSettings
{
    public int Language { get; set; }
    public int Volume { get; set; }
    public int FillLightMode { get; set; }
    public int MaskDetectionMode { get; set; }
    public int TemperatureDetectionMode { get; set; }
    public int TemperatureAlarmThresholdX10 { get; set; }
    public int TemperatureDisplayMode { get; set; }
    public int FaceIdentifyRange { get; set; }
    public int LivenessDetectionMode { get; set; }
    public int LivenessSimilarity { get; set; }
}

// Leitura reversa / auditoria de pessoal.
public record PersonnelAudit(List<uint> MissingOnDevice, List<uint> ExtraOnDevice, int DeviceCount, int ExpectedCount);

// Foto do evento.
public record EventPhotoListItem(Guid Id, uint? UserCode, DateTime CapturedAtUtc, int RawEventCode, DateTime DownloadedAtUtc);
public record DownloadPhotosResult(int Downloaded);
