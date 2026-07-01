namespace HospitalAccess.Web.Services;

public record LoginResponse(string Token, string Role);

public record ControllerDoorDto(Guid Id, string Name, int RelayIndex);
public record ControllerDto(
    Guid Id, string Name, string IpAddress, int Port, string SerialNumber,
    bool SupportsWaitRepeatMessage, IEnumerable<ControllerDoorDto> Doors);

public record CreateControllerRequest(
    string Name, string IpAddress, int Port, string SerialNumber,
    string CommunicationPassword, bool SupportsWaitRepeatMessage);

public record DoorDto(Guid Id, string Name, int RelayIndex, string ControllerName);
public record CreateDoorRequest(string Name, Guid ControllerId, int RelayIndex);

public record CreateUserResponse(Guid Id, uint UserCode);
public record CreateVisitorRequest(string Name, DateTime ValidUntil, int TimeGroup);

public record SyncStatusDto(Guid UserId, string UserName, string State, int RetryCount, string? LastError, DateTime UpdatedAt);

public record AccessLogItem(
    Guid Id, DateTime TimestampUtc, uint? UserCode, string? UserName,
    Guid ControllerId, string ControllerName, string? DoorName,
    string Method, int RawEventCode, int? Direction, bool Granted);

public record AccessLogPage(int Total, int Page, int PageSize, List<AccessLogItem> Items);
