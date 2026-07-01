using DoNetDrive.Core;
using DoNetDrive.Core.Command;
using DoNetDrive.Protocol;
using HospitalAccess.Domain.Entities;

namespace HospitalAccess.Gateway.Connections;

/// <summary>
/// Monta o objeto de detalhe de comando (CommandDetail) do SDK para um controlador.
///
/// Baseado em TestClass.cs / FrmMain.cs do projeto de exemplo
/// (DoNetDrive.Protocol.Fingerprint.Test).
///
/// ConnectType.TCPClient confirmado via engenharia reversa do IL de
/// DoNetDrive.Protocol.dll (enum CommandDetailFactory/ConnectType tem exatamente
/// 3 membros: TCPClient=0, TCPServerClient=1, UDPClient=2). TCPServerClient é para
/// quando o CONTROLADOR disca para o nosso software (modo "phone home"); como os 30
/// controladores são on-premise com IP fixo conhecido, nós discamos para eles, logo
/// TCPClient é o correto — TestClass.cs só demonstra UDPClient, não copiar isso.
/// </summary>
public sealed class ControllerConnectionFactory
{
    public INCommandDetail CreateCommandDetail(Controller controller)
    {
        var cmdDtl = CommandDetailFactory.CreateDetail(
            CommandDetailFactory.ConnectType.TCPClient,
            controller.IpAddress,
            controller.Port,
            CommandDetailFactory.ControllerType.A33_Face,
            controller.SerialNumber,
            controller.CommunicationPassword);

        cmdDtl.Timeout = controller.TimeoutMs;
        cmdDtl.RestartCount = controller.RestartCount;
        return cmdDtl;
    }
}
