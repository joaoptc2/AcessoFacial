namespace HospitalAccess.Domain.Enums;

/// <summary>
/// Como o servidor estabelece a comunicação com o controlador 8190H.
///
/// O protocolo (§1.2) define os modos do dispositivo como UDP, TCP Client e COM. Aqui a
/// perspectiva é a do SERVIDOR (SDK <c>CommandDetailFactory.ConnectType</c>):
/// </summary>
public enum ControllerConnectionMode
{
    /// <summary>
    /// Servidor disca ativamente para o IP:porta do controlador (SDK: <c>TCPClient</c>).
    /// É o padrão atual; assume que o 8190H aceita conexões TCP de entrada e empurra os
    /// eventos por essa mesma conexão — comportamento NÃO validado contra hardware real
    /// (ver README, seção 2). Se os comandos derem timeout ou o push não chegar, troque
    /// para <see cref="TcpServerClient"/>.
    /// </summary>
    TcpClient = 0,

    /// <summary>
    /// O controlador disca para o nosso servidor TCP (modo "phone home"; SDK:
    /// <c>TCPServerClient</c>). Exige que o controlador esteja configurado com o IP/porta
    /// do servidor (ver <c>WriteNetworkSettings</c>) e que o servidor esteja escutando.
    /// É o modo que o protocolo descreve para push em tempo real ao servidor configurado.
    /// </summary>
    TcpServerClient = 1,

    /// <summary>Comunicação por datagramas UDP (SDK: <c>UDPClient</c>).</summary>
    Udp = 2,
}
