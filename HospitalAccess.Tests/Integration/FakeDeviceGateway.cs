using System.Collections.Concurrent;
using HospitalAccess.Domain.Entities;
using HospitalAccess.Gateway;

namespace HospitalAccess.Tests.Integration;

/// <summary>
/// Gateway falso para exercitar o <c>UserSyncService</c> sem hardware. Implementa só o que o
/// serviço de sincronização usa; o resto lança, para que um teste que dependa de um caminho não
/// coberto falhe alto em vez de passar por acidente.
///
/// <para>
/// Os desfechos configuráveis são exatamente os que o serviço trata de formas DIFERENTES:
/// sucesso, falha transitória (exceção — backoff), face duplicada e falha permanente
/// (quarentena). É a matriz que decide se uma credencial fica ativa ou não no aparelho.
/// </para>
/// </summary>
public sealed class FakeDeviceGateway : IDeviceGateway
{
    /// <summary>Chamadas de cadastro com face, na ordem — (controlador, código do usuário).</summary>
    public ConcurrentQueue<(Guid ControllerId, uint UserCode)> FaceUploads { get; } = new();

    /// <summary>Chamadas de cadastro sem face (visitantes).</summary>
    public ConcurrentQueue<(Guid ControllerId, uint UserCode)> FacelessUploads { get; } = new();

    /// <summary>Remoções pedidas ao aparelho — a prova de que a credencial saiu do hardware.</summary>
    public ConcurrentQueue<(Guid ControllerId, uint UserCode)> Deletions { get; } = new();

    public ConcurrentQueue<Guid> ClearedControllers { get; } = new();

    /// <summary>Resultado do próximo <c>AddPersonWithFaceAsync</c>, por controlador. Ausente = sucesso.</summary>
    public ConcurrentDictionary<Guid, AddFaceResult> FaceResultByController { get; } = new();

    /// <summary>Controladores que devem LANÇAR (simula aparelho offline/timeout = falha transitória).</summary>
    public ConcurrentDictionary<Guid, string> ThrowingControllers { get; } = new();

    /// <summary>Controladores cuja REMOÇÃO deve lançar (revogação que não chega ao aparelho).</summary>
    public ConcurrentDictionary<Guid, string> ThrowingOnDelete { get; } = new();

    public Task<AddFaceResult> AddPersonWithFaceAsync(Controller controller, User user, byte[] faceJpg, CancellationToken ct = default)
    {
        if (ThrowingControllers.TryGetValue(controller.Id, out var message))
            throw new DeviceCommandException(message);

        FaceUploads.Enqueue((controller.Id, user.UserCode));
        return Task.FromResult(FaceResultByController.TryGetValue(controller.Id, out var result)
            ? result
            : new AddFaceResult(true, FaceUploadCode.Ok, null));
    }

    public Task AddPersonWithoutFaceAsync(Controller controller, User user, CancellationToken ct = default)
    {
        if (ThrowingControllers.TryGetValue(controller.Id, out var message))
            throw new DeviceCommandException(message);

        FacelessUploads.Enqueue((controller.Id, user.UserCode));
        return Task.CompletedTask;
    }

    public Task DeletePersonAsync(Controller controller, uint userCode, CancellationToken ct = default)
    {
        if (ThrowingOnDelete.TryGetValue(controller.Id, out var message))
            throw new DeviceCommandException(message);

        Deletions.Enqueue((controller.Id, userCode));
        return Task.CompletedTask;
    }

    public Task ClearAllPersonsAsync(Controller controller, CancellationToken ct = default)
    {
        ClearedControllers.Enqueue(controller.Id);
        return Task.CompletedTask;
    }

    public DateTime? GetCircuitOpenUntilUtc(Guid controllerId) => null;
    public DateTime? GetLastPushActivityUtc(string serialNumber) => null;
    public DateTime? GetLastEventPushUtc(string serialNumber) => null;
    public void EnsurePushChannel(Controller controller) { }
    public void ForgetController(Controller controller) { }

    // ---- Não usados pelo caminho de sincronização: falham alto se algum teste os alcançar. ----

    private static Task<T> NotUsed<T>([System.Runtime.CompilerServices.CallerMemberName] string? member = null) =>
        throw new NotSupportedException($"{member} não é usado pelo UserSyncService — o teste tomou um caminho inesperado.");

    private static Task NotUsed([System.Runtime.CompilerServices.CallerMemberName] string? member = null) =>
        throw new NotSupportedException($"{member} não é usado pelo UserSyncService — o teste tomou um caminho inesperado.");

    public Task<string> ReadSerialNumberAsync(Controller controller, CancellationToken ct = default) => NotUsed<string>();
    public Task TriggerFireAlarmAsync(Controller controller, CancellationToken ct = default) => NotUsed();
    public Task<IReadOnlyList<DeviceAccessEvent>> CollectAccessRecordsAsync(Controller controller, CancellationToken ct = default) => NotUsed<IReadOnlyList<DeviceAccessEvent>>();
    public Task OpenDoorAsync(Controller controller, CancellationToken ct = default) => NotUsed();
    public Task CloseDoorAsync(Controller controller, CancellationToken ct = default) => NotUsed();
    public Task HoldDoorOpenAsync(Controller controller, CancellationToken ct = default) => NotUsed();
    public Task LockDoorAsync(Controller controller, CancellationToken ct = default) => NotUsed();
    public Task UnlockDoorAsync(Controller controller, CancellationToken ct = default) => NotUsed();
    public Task<ControllerNetworkInfo> ReadNetworkSettingsAsync(Controller controller, CancellationToken ct = default) => NotUsed<ControllerNetworkInfo>();
    public Task WriteNetworkSettingsAsync(Controller controller, ControllerNetworkInfo info, CancellationToken ct = default) => NotUsed();
    public Task<IReadOnlyList<DiscoveredController>> DiscoverControllersAsync(int udpPort, TimeSpan scanDuration, CancellationToken ct = default) => NotUsed<IReadOnlyList<DiscoveredController>>();
    public Task<DateTime> ReadControllerTimeAsync(Controller controller, CancellationToken ct = default) => NotUsed<DateTime>();
    public Task SyncControllerTimeAsync(Controller controller, CancellationToken ct = default) => NotUsed();
    public Task SyncHolidaysAsync(Controller controller, IReadOnlyList<HolidayEntry> holidays, CancellationToken ct = default) => NotUsed();
    public Task SyncTimeGroupsAsync(Controller controller, IReadOnlyList<TimeGroupEntry> groups, CancellationToken ct = default) => NotUsed();
    public Task<AlarmSettingsSnapshot> ReadAlarmSettingsAsync(Controller controller, CancellationToken ct = default) => NotUsed<AlarmSettingsSnapshot>();
    public Task WriteAlarmSettingsAsync(Controller controller, AlarmSettingsSnapshot settings, CancellationToken ct = default) => NotUsed();
    public Task ClearAlarmAsync(Controller controller, CancellationToken ct = default) => NotUsed();
    public Task<IReadOnlyList<CapturedEventPhoto>> ReadRecentEventPhotosAsync(Controller controller, int quantity, CancellationToken ct = default) => NotUsed<IReadOnlyList<CapturedEventPhoto>>();
    public Task<IReadOnlyList<uint>> ReadRegisteredUserCodesAsync(Controller controller, CancellationToken ct = default) => NotUsed<IReadOnlyList<uint>>();
    public Task<KioskSettingsSnapshot> ReadKioskSettingsAsync(Controller controller, CancellationToken ct = default) => NotUsed<KioskSettingsSnapshot>();
    public Task WriteKioskSettingsAsync(Controller controller, KioskSettingsSnapshot settings, CancellationToken ct = default) => NotUsed();
    public Task StartMonitoringAsync(Controller controller, CancellationToken ct = default) => NotUsed();
    public Task StopMonitoringAsync(Controller controller, CancellationToken ct = default) => NotUsed();
    public Task<bool> IsMonitoringActiveAsync(Controller controller, CancellationToken ct = default) => NotUsed<bool>();

#pragma warning disable CS0067 // os eventos existem no contrato; o caminho de sync não os dispara
    public event EventHandler<DeviceAccessEvent>? AccessEventReceived;
    public event EventHandler<DeviceAlarmEvent>? AlarmEventReceived;
#pragma warning restore CS0067
}
