using HospitalAccess.Domain.Enums;

namespace HospitalAccess.Gateway;

/// <summary>Evento de acesso normalizado, traduzido do "authentication record" do protocolo.</summary>
public sealed class DeviceAccessEvent
{
    public required string ControllerSerialNumber { get; init; }
    public DateTime TimestampUtc { get; init; }
    public uint? UserCode { get; init; }
    public AccessMethod Method { get; init; }
    public int RawEventCode { get; init; }
    public int? Direction { get; init; } // 1 = entrada, 2 = saída
    public bool Granted { get; init; }
}
