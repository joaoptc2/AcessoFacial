using System.Text;
using DoNetDrive.Core;
using DoNetDrive.Core.Connector;
using DoNetDrive.Core.Data;
using DoNetDrive.Protocol.Door8800;
using DoNetDrive.Protocol.Door.Door8800.SystemParameter.SN;
using DoNetDrive.Protocol.Fingerprint.Data.Transaction;
using DoNetDrive.Protocol.Fingerprint.Door.Remote;
using DoNetDrive.Protocol.Fingerprint.Person;
using HospitalAccess.Domain.Entities;
using HospitalAccess.Domain.Enums;
using HospitalAccess.Gateway.Connections;
using HospitalAccess.Gateway.Imaging;
using PersonData = DoNetDrive.Protocol.Fingerprint.Data.Person;

namespace HospitalAccess.Gateway;

/// <summary>
/// Implementação concreta do gateway usando o SDK novo DoNetDrive.*.
/// ÚNICO componente que toca o hardware. Assinaturas confirmadas em TestClass.cs,
/// frmPerson.cs, frmDoor.cs e ConnectorAllocatorTestClass.cs do projeto de exemplo
/// DoNetDrive.Protocol.Fingerprint.Test, mais engenharia reversa (monodis) do IL das
/// DLLs reais para o enum CommandDetailFactory.ConnectType (ver ControllerConnectionFactory).
/// </summary>
public sealed class DoNetDriveGateway : IDeviceGateway, IDisposable
{
    private readonly ControllerConnectionFactory _connections;
    private readonly ConnectorAllocator _allocator;

    /// <summary>
    /// Códigos de TransactionCode (Classe 9 authentication record) que representam
    /// acesso CONCEDIDO. Extraído dos comentários de frmRecord.cs (mCardTransactionList),
    /// cross-validado com a Classe 8/9 do protocolo. 13 é a combinação "face+digital+senha"
    /// que o próprio fabricante documenta como "não abre a porta" — tratado como negado.
    /// </summary>
    private static readonly HashSet<int> GrantedTransactionCodes =
        new() { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 14, 25, 29 };

    public DoNetDriveGateway(ControllerConnectionFactory connections)
    {
        _connections = connections;
        _allocator = ConnectorAllocator.GetAllocator();
        _allocator.TransactionMessage += OnTransactionMessage;
    }

    public event EventHandler<DeviceAccessEvent>? AccessEventReceived;

    public async Task<string> ReadSerialNumberAsync(Controller controller, CancellationToken ct = default)
    {
        var cmdDtl = _connections.CreateCommandDetail(controller);
        var cmd = new ReadSN(cmdDtl);
        await _allocator.AddCommandAsync(cmd);

        var result = cmd.getResult() as SN_Result
            ?? throw new InvalidOperationException(
                $"ReadSN não retornou resultado para o controlador '{controller.Name}' ({controller.IpAddress}:{controller.Port}).");

        return Encoding.ASCII.GetString(result.SNBuf).TrimEnd('\0');
    }

    public async Task<AddFaceResult> AddPersonWithFaceAsync(Controller controller, User user, byte[] faceJpg, CancellationToken ct = default)
    {
        // Requisito de hardware: JPG, 480x640, <= 120KB (Classe 11 / Appendix do protocolo).
        var converted = FaceImageConverter.ConvertImage(faceJpg, 480, 640, 122880);

        var person = new PersonData
        {
            UserCode = user.UserCode,
            PName = user.Name,
            TimeGroup = user.TimeGroup,
        };
        if (user.ValidUntil is { } validUntil)
            person.Expiry = validUntil;

        var identification = new IdentificationData(1, converted); // File Type 1 = foto de pessoa
        var par = new AddPersonAndImage_Parameter(person, identification)
        {
            // WaitRepeatMessage só funciona em firmware >= v4.28 (aviso do README).
            WaitRepeatMessage = controller.SupportsWaitRepeatMessage,
        };

        var cmdDtl = _connections.CreateCommandDetail(controller);
        var cmd = new AddPeosonAndImage(cmdDtl, par); // grafia confirmada no SDK
        await _allocator.AddCommandAsync(cmd);

        var result = cmd.getResult() as AddPersonAndImage_Result;
        return MapAddFaceResult(result);
    }

    private static AddFaceResult MapAddFaceResult(AddPersonAndImage_Result? result)
    {
        if (result is null)
        {
            // Handle 0 no protocolo (Classe 11 / 0x3B 0x01) = usuário inexistente / recusado.
            return new AddFaceResult(false, FaceUploadCode.UserNotFound,
                "Controlador recusou a operação (handle 0 / usuário inexistente).");
        }

        var status = result.IdDataUploadStatus[0];
        return status switch
        {
            1 => new AddFaceResult(true, FaceUploadCode.Ok, null),
            2 => new AddFaceResult(false, FaceUploadCode.FeatureCodeUnidentifiable,
                "Feature code não identificável na foto."),
            3 => new AddFaceResult(false, FaceUploadCode.NoFaceInPhoto,
                "Nenhum rosto reconhecível na foto."),
            4 => new AddFaceResult(false, FaceUploadCode.Duplicate,
                $"Foto/feature code duplicado (usuário existente: {result.IdDataRepeatUser[0]})."),
            _ => new AddFaceResult(false, FaceUploadCode.CrcFailure,
                $"Falha de verificação CRC32 no upload (status retornado: {status})."),
        };
    }

    public async Task DeletePersonAsync(Controller controller, uint userCode, CancellationToken ct = default)
    {
        var cmdDtl = _connections.CreateCommandDetail(controller);
        var person = new PersonData { UserCode = userCode };
        var par = new DeletePerson_Parameter(new List<PersonData> { person });
        var cmd = new DeletePerson(cmdDtl, par);
        await _allocator.AddCommandAsync(cmd);
    }

    public async Task OpenDoorAsync(Controller controller, CancellationToken ct = default)
    {
        // O 8190H (A33_Face) tem um único relé de porta por controlador; o comando de
        // desbloqueio remoto (Classe 3 / Remote Unlock) não recebe índice de relé —
        // confirmado tanto no .doc (0x03/0x03/0x00, sem parâmetro) quanto no SDK
        // (OpenDoor(cmdDtl), sem argumento).
        var cmdDtl = _connections.CreateCommandDetail(controller);
        var cmd = new OpenDoor(cmdDtl);
        await _allocator.AddCommandAsync(cmd);
    }

    public async Task CloseDoorAsync(Controller controller, CancellationToken ct = default)
    {
        var cmdDtl = _connections.CreateCommandDetail(controller);
        var cmd = new CloseDoor(cmdDtl);
        await _allocator.AddCommandAsync(cmd);
    }

    public async Task HoldDoorOpenAsync(Controller controller, CancellationToken ct = default)
    {
        var cmdDtl = _connections.CreateCommandDetail(controller);
        var cmd = new HoldDoor(cmdDtl);
        await _allocator.AddCommandAsync(cmd);
    }

    public async Task LockDoorAsync(Controller controller, CancellationToken ct = default)
    {
        var cmdDtl = _connections.CreateCommandDetail(controller);
        var cmd = new LockDoor(cmdDtl);
        await _allocator.AddCommandAsync(cmd);
    }

    public async Task UnlockDoorAsync(Controller controller, CancellationToken ct = default)
    {
        var cmdDtl = _connections.CreateCommandDetail(controller);
        var cmd = new UnlockDoor(cmdDtl);
        await _allocator.AddCommandAsync(cmd);
    }

    /// <summary>
    /// Evento em tempo real empurrado pelo controlador (Classe 9 / authentication record).
    /// Padrão confirmado em ConnectorAllocatorTestClass.cs: o cast para CardTransaction é
    /// feito diretamente sobre o INData original do evento, não sobre uma sub-propriedade.
    /// </summary>
    private void OnTransactionMessage(INConnectorDetail connectorDetail, INData eventData)
    {
        if (eventData is not Door8800Transaction transaction) return;
        if (transaction.CmdIndex != 1) return; // 1 = registro de autenticação (cartão/face/QR)
        if (eventData is not CardTransaction card) return;

        var evt = new DeviceAccessEvent
        {
            ControllerSerialNumber = transaction.SN,
            TimestampUtc = card.TransactionDate.ToUniversalTime(),
            UserCode = card.UserCode,
            Method = card.TransactionCode switch
            {
                3 => AccessMethod.Face,
                1 => AccessMethod.QrCode, // QR de visitante é apresentado ao leitor como "cartão"
                _ => AccessMethod.Card,
            },
            RawEventCode = card.TransactionCode,
            Direction = card.Accesstype,
            Granted = GrantedTransactionCodes.Contains(card.TransactionCode),
        };

        AccessEventReceived?.Invoke(this, evt);
    }

    public void Dispose()
    {
        _allocator.TransactionMessage -= OnTransactionMessage;
        _allocator.Dispose();
    }
}
