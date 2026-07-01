using HospitalAccess.Domain.Entities;

namespace HospitalAccess.Gateway;

/// <summary>
/// Contrato que a aplicação usa para falar com os controladores.
/// A implementação concreta (DoNetDriveGateway) é o ÚNICO ponto que toca o SDK/hardware.
/// Tudo é assíncrono e por-controlador.
/// </summary>
public interface IDeviceGateway
{
    /// <summary>Prova de conceito: lê o SN de um controlador (primeiro alvo de implementação).</summary>
    Task<string> ReadSerialNumberAsync(Controller controller, CancellationToken ct = default);

    /// <summary>
    /// Cadastra/atualiza um usuário com foto de face em um controlador.
    /// A imagem já deve vir convertida (480x640, &lt;=120KB) OU o gateway converte internamente.
    /// </summary>
    Task<AddFaceResult> AddPersonWithFaceAsync(Controller controller, User user, byte[] faceJpg, CancellationToken ct = default);

    /// <summary>Remove um usuário de um controlador.</summary>
    Task DeletePersonAsync(Controller controller, uint userCode, CancellationToken ct = default);

    /// <summary>Abre uma porta (relé) remotamente.</summary>
    Task OpenDoorAsync(Controller controller, int relayIndex, CancellationToken ct = default);

    /// <summary>
    /// Evento de acesso em tempo real empurrado por um controlador.
    /// A implementação assina os eventos do SDK e dispara este callback.
    /// </summary>
    event EventHandler<DeviceAccessEvent> AccessEventReceived;
}

/// <summary>Resultado do cadastro de face, mapeando os códigos de retorno do protocolo.</summary>
public sealed record AddFaceResult(bool Success, FaceUploadCode Code, string? Message);

/// <summary>Códigos de retorno do upload de foto/feature code (Classe 11 / verificação CRC32).</summary>
public enum FaceUploadCode
{
    Ok = 1,
    FeatureCodeUnidentifiable = 2, // 2 -- feature code can not be identified
    NoFaceInPhoto = 3,             // 3 -- personnel photo can not be identified
    Duplicate = 4,                 // 4 -- duplicate personnel photo or feature code
    CrcFailure = 0,                // 0 -- check failure (CRC32 inconsistente)
    UserNotFound = -1              // handle 0 = usuário inexistente / operação recusada
}
