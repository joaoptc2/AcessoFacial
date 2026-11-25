namespace HospitalAccess.Domain.Enums;

/// <summary>
/// Canal de EVENTOS de acesso (clock-in/out) de um controlador — não confundir com
/// <see cref="ControllerConnectionMode"/>, que é como o servidor fala o protocolo binário por TCP.
/// O comando (abrir porta, cadastro de usuário) sempre vai pelo SDK/TCP, nos dois casos — só a
/// CAPTURA de evento muda. Piloto 2026-11: comparar perda de evento em portas que caem muito.
/// </summary>
public enum EventChannel
{
    /// <summary>Evento capturado pelo SDK binário (push <c>BeginWatch</c> + coleta offline). Padrão.</summary>
    Sdk = 0,

    /// <summary>
    /// Evento recebido pelo phone-home HTTP (<c>POST /note/insertNoteFace</c>,
    /// <see cref="HospitalAccess.Domain.Entities.Controller.EventChannel"/> precisa estar
    /// configurado para isto E o aparelho precisa estar configurado localmente para discar nesse
    /// modo — não há comando no SDK para configurar isto remotamente).
    /// </summary>
    HttpPhoneHome = 1,
}
