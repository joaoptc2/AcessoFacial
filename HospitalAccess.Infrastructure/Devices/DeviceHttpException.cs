namespace HospitalAccess.Infrastructure.Devices;

/// <summary>
/// Falha ao comunicar com a API REST local do painel web do controlador FC-8190H.
/// Carrega, quando disponível, o status HTTP e o <c>errCode</c> do envelope do firmware
/// (o aparelho responde HTTP 200 mesmo em erro — a falha vem no corpo em <c>result:false</c>).
/// </summary>
public sealed class DeviceHttpException : Exception
{
    public int? Status { get; }
    public int? ErrCode { get; }

    public DeviceHttpException(string message, int? status = null, int? errCode = null, Exception? inner = null)
        : base(message, inner)
    {
        Status = status;
        ErrCode = errCode;
    }
}
