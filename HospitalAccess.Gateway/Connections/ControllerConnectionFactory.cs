using DoNetDrive.Core;
using DoNetDrive.Core.Command;
using DoNetDrive.Protocol;
using HospitalAccess.Domain.Entities;
using HospitalAccess.Domain.Enums;

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
        INCommandDetail cmdDtl;
        try
        {
            cmdDtl = CommandDetailFactory.CreateDetail(
                MapConnectType(controller.ConnectionMode),
                controller.IpAddress,
                controller.Port,
                CommandDetailFactory.ControllerType.A33_Face,
                controller.SerialNumber,
                controller.CommunicationPassword);
        }
        catch (ArgumentException ex)
        {
            // O SDK valida o formato da senha/SN em set_Password/set_SN e lança ArgumentException
            // (ex.: "Password Is Error") ANTES de conectar. Causa comum: a senha de comunicação não
            // pôde ser descriptografada porque a chave da DataProtection mudou (redeploy sem chaveiro
            // persistido) — o valor lido é o texto cifrado, não a senha. Traduz para uma mensagem útil.
            throw new DeviceCommandException(
                $"Senha de comunicação inválida para o controlador '{controller.Name}' ({controller.IpAddress}:{controller.Port}). " +
                "Reentre a senha do controlador — se ela estava criptografada e a chave de proteção de dados foi perdida (redeploy), " +
                "as senhas antigas ficam indecifráveis e precisam ser cadastradas de novo.", ex);
        }

        cmdDtl.Timeout = controller.TimeoutMs;
        cmdDtl.RestartCount = controller.RestartCount;
        return cmdDtl;
    }

    private static CommandDetailFactory.ConnectType MapConnectType(ControllerConnectionMode mode) => mode switch
    {
        ControllerConnectionMode.TcpServerClient => CommandDetailFactory.ConnectType.TCPServerClient,
        ControllerConnectionMode.Udp => CommandDetailFactory.ConnectType.UDPClient,
        _ => CommandDetailFactory.ConnectType.TCPClient,
    };

    /// <summary>
    /// Monta um CommandDetail de broadcast UDP para descoberta de controladores desconhecidos
    /// na rede local (SN "0000000000000000" e senha "FFFFFFFF" são os valores especiais de
    /// broadcast usados pelo próprio SDK — ver FrmMain.cs/SearchCommandDetail do projeto de
    /// exemplo). Não validado contra hardware real.
    /// </summary>
    public INCommandDetail CreateBroadcastDetail(int udpPort)
    {
        var cmdDtl = CommandDetailFactory.CreateDetail(
            CommandDetailFactory.ConnectType.UDPClient,
            "255.255.255.255",
            udpPort,
            CommandDetailFactory.ControllerType.A33_Face,
            "0000000000000000",
            "FFFFFFFF");

        cmdDtl.Timeout = 2000;
        cmdDtl.RestartCount = 1;
        return cmdDtl;
    }
}
