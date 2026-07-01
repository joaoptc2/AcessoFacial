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

    /// <summary>Abre a porta remotamente (pulso — volta a fechar após o tempo de liberação configurado).</summary>
    Task OpenDoorAsync(Controller controller, CancellationToken ct = default);

    /// <summary>Fecha a porta remotamente (encerra o modo "sempre aberto", se ativo).</summary>
    Task CloseDoorAsync(Controller controller, CancellationToken ct = default);

    /// <summary>Mantém a porta aberta (modo "sempre aberto") até um comando de fechar ou trancar.</summary>
    Task HoldDoorOpenAsync(Controller controller, CancellationToken ct = default);

    /// <summary>Tranca a porta — bloqueia inclusive aberturas por credencial válida até destrancar.</summary>
    Task LockDoorAsync(Controller controller, CancellationToken ct = default);

    /// <summary>Destranca a porta (reverte LockDoorAsync), voltando à operação normal.</summary>
    Task UnlockDoorAsync(Controller controller, CancellationToken ct = default);

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
