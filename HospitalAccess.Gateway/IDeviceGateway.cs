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

    /// <summary>Lê a configuração de rede atual do controlador (Classe I / TCPSetting).</summary>
    Task<ControllerNetworkInfo> ReadNetworkSettingsAsync(Controller controller, CancellationToken ct = default);

    /// <summary>Reconfigura a rede do controlador remotamente. Cuidado: IP errado torna o controlador inacessível até acesso físico.</summary>
    Task WriteNetworkSettingsAsync(Controller controller, ControllerNetworkInfo info, CancellationToken ct = default);

    /// <summary>
    /// Varredura por broadcast UDP para descobrir controladores na rede local (Apêndices 3/5).
    /// Não validado contra hardware real neste ambiente — ver README.
    /// </summary>
    Task<IReadOnlyList<DiscoveredController>> DiscoverControllersAsync(int udpPort, TimeSpan scanDuration, CancellationToken ct = default);

    /// <summary>Lê o relógio atual do controlador (Classe II).</summary>
    Task<DateTime> ReadControllerTimeAsync(Controller controller, CancellationToken ct = default);

    /// <summary>Sincroniza o relógio do controlador com o horário deste servidor.</summary>
    Task SyncControllerTimeAsync(Controller controller, CancellationToken ct = default);

    /// <summary>Substitui todos os feriados do controlador (Classe V) pela lista informada.</summary>
    Task SyncHolidaysAsync(Controller controller, IReadOnlyList<HolidayEntry> holidays, CancellationToken ct = default);

    /// <summary>Substitui os 64 grupos de horário do controlador (Classe VI) pela definição informada.</summary>
    Task SyncTimeGroupsAsync(Controller controller, IReadOnlyList<TimeGroupEntry> groups, CancellationToken ct = default);

    /// <summary>Lê a configuração atual dos alarmes de hardware (Classe IIII).</summary>
    Task<AlarmSettingsSnapshot> ReadAlarmSettingsAsync(Controller controller, CancellationToken ct = default);

    /// <summary>Grava a configuração dos alarmes de hardware.</summary>
    Task WriteAlarmSettingsAsync(Controller controller, AlarmSettingsSnapshot settings, CancellationToken ct = default);

    /// <summary>Silencia/encerra alarmes ativos no controlador.</summary>
    Task ClearAlarmAsync(Controller controller, CancellationToken ct = default);

    /// <summary>Baixa as fotos mais recentes capturadas em eventos de acesso (Classe XI).</summary>
    Task<IReadOnlyList<CapturedEventPhoto>> ReadRecentEventPhotosAsync(Controller controller, int quantity, CancellationToken ct = default);

    /// <summary>Lê os códigos de usuário efetivamente cadastrados no controlador (Classe VII), para auditoria/detecção de divergência.</summary>
    Task<IReadOnlyList<uint>> ReadRegisteredUserCodesAsync(Controller controller, CancellationToken ct = default);

    /// <summary>Lê os ajustes locais do quiosque (idioma, volume, luz, máscara, temperatura etc.).</summary>
    Task<KioskSettingsSnapshot> ReadKioskSettingsAsync(Controller controller, CancellationToken ct = default);

    /// <summary>Grava os ajustes locais do quiosque.</summary>
    Task WriteKioskSettingsAsync(Controller controller, KioskSettingsSnapshot settings, CancellationToken ct = default);

    /// <summary>
    /// Habilita o monitoramento em tempo real (BeginWatch / Online Transaction) neste controlador
    /// e mantém a conexão aberta para receber o push de eventos de acesso e alarme. Deve ser
    /// (re)chamado na subida do serviço e periodicamente, já que o dispositivo NÃO persiste o
    /// estado de monitoramento após reboot (protocolo §10). Não validado contra hardware real.
    /// </summary>
    Task StartMonitoringAsync(Controller controller, CancellationToken ct = default);

    /// <summary>Desativa o monitoramento em tempo real (CloseWatch) e libera a conexão persistente.</summary>
    Task StopMonitoringAsync(Controller controller, CancellationToken ct = default);

    /// <summary>
    /// Evento de acesso em tempo real empurrado por um controlador.
    /// A implementação assina os eventos do SDK e dispara este callback.
    /// </summary>
    event EventHandler<DeviceAccessEvent> AccessEventReceived;

    /// <summary>Evento de alarme de hardware em tempo real empurrado por um controlador.</summary>
    event EventHandler<DeviceAlarmEvent> AlarmEventReceived;
}

/// <summary>
/// Erro de execução de um comando no controlador (timeout, cancelamento, falha de autenticação
/// ou comando não confirmado pelo dispositivo). Lançado quando o SDK não confirma o sucesso —
/// substitui o antigo "sucesso silencioso" em que uma escrita que falhou passava como concluída.
/// </summary>
public sealed class DeviceCommandException : Exception
{
    public DeviceCommandException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>Evento de alarme normalizado, traduzido do registro de sistema (Classe VIII/IX) do protocolo.</summary>
public sealed class DeviceAlarmEvent
{
    public required string ControllerSerialNumber { get; init; }
    public DateTime TimestampUtc { get; init; }
    public Domain.Enums.AlarmKind Kind { get; init; }
    public int RawEventCode { get; init; }
    public bool Cleared { get; init; }
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
