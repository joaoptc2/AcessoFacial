namespace HospitalAccess.Web.Services;

public record LoginResponse(string Token, string Role);

// Controladores (cada um representa fisicamente uma única porta).
public record ControllerDto(
    Guid Id, string Name, string IpAddress, int Port, string SerialNumber,
    bool SupportsWaitRepeatMessage, int RelayIndex, int TimeoutMs, int RestartCount, int UserCount);

public record ControllerDetailDto(
    Guid Id, string Name, string IpAddress, int Port, string SerialNumber, string CommunicationPassword,
    bool SupportsWaitRepeatMessage, int RelayIndex, int TimeoutMs, int RestartCount);

public record CreateControllerRequest(
    string Name, string IpAddress, int Port, string SerialNumber,
    string CommunicationPassword, bool SupportsWaitRepeatMessage, int RelayIndex = 0);

public record UpdateControllerRequest(
    string Name, string IpAddress, int Port, string SerialNumber,
    string CommunicationPassword, bool SupportsWaitRepeatMessage, int RelayIndex,
    int TimeoutMs, int RestartCount);

// Grupos organizacionais de usuários.
public record UserGroupDto(Guid Id, string Name, string? Description, int UserCount);
public record UserGroupRequest(string Name, string? Description);

// Usuários permanentes.
public record UserControllerRef(Guid ControllerId, string ControllerName);
public record UserListItemDto(
    Guid Id, uint UserCode, string Name, int TimeGroup, Guid? GroupId, string? GroupName,
    bool HasFacePhoto, string? CreatedByUsername, DateTime CreatedAtUtc, DateTime? RevokedAtUtc,
    IEnumerable<UserControllerRef> Controllers);

public record UserDetailDto(
    Guid Id, uint UserCode, string Name, int TimeGroup, Guid? GroupId, bool HasFacePhoto,
    IEnumerable<Guid> ControllerIds);

public record CreateUserResponse(Guid Id, uint UserCode);

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
