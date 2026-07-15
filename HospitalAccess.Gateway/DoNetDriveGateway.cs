using System.Collections.Concurrent;
using System.Text;
using DoNetDrive.Core;
using DoNetDrive.Core.Command;
using DoNetDrive.Core.Connector;
using DoNetDrive.Core.Data;
using DoNetDrive.Protocol.Door8800;
using DoNetDrive.Protocol.Door.Door8800.Data;
using DoNetDrive.Protocol.Door.Door8800.Holiday;
using DoNetDrive.Protocol.Door.Door8800.SystemParameter.SearchControltor;
using DoNetDrive.Protocol.Door.Door8800.SystemParameter.SN;
using DoNetDrive.Protocol.Door.Door8800.SystemParameter.TCPSetting;
using DoNetDrive.Protocol.Door.Door8800.Time;
using DoNetDrive.Protocol.Door.Door8800.TimeGroup;
using DoNetDrive.Protocol.Fingerprint.Data.Transaction;
using DoNetDrive.Protocol.Fingerprint.Door.Remote;
using DoNetDrive.Protocol.Fingerprint.Person;
using DoNetDrive.Protocol.Fingerprint.SystemParameter;
using DoNetDrive.Protocol.Fingerprint.SystemParameter.Watch;
using DoNetDrive.Protocol.Fingerprint.Transaction;
using DotNetty.Buffers;
using HospitalAccess.Domain.Entities;
using HospitalAccess.Domain.Enums;
using HospitalAccess.Domain.Protocol;
using HospitalAccess.Gateway.Connections;
using HospitalAccess.Gateway.Imaging;
using PersonData = DoNetDrive.Protocol.Fingerprint.Data.Person;
using DataTimeGroup = DoNetDrive.Protocol.Door.Door8800.Data.TimeGroup;
using AlarmNs = DoNetDrive.Protocol.Fingerprint.Alarm;
using AbstractTransaction = DoNetDrive.Protocol.Transaction.AbstractTransaction;
// Desambigua entre os tipos do facial (Fingerprint) e os das controladoras de cartão (Door8800),
// que têm o mesmo nome. O push do 8190H entrega os tipos do namespace Fingerprint.
using CardTransaction = DoNetDrive.Protocol.Fingerprint.Data.Transaction.CardTransaction;
using SystemTransaction = DoNetDrive.Protocol.Fingerprint.Data.Transaction.SystemTransaction;
// Leitura de registros offline (Classe VIII): o tipo de banco e o resultado ficam no namespace
// Door8800 (o comando ReadTransactionDatabase em si é o do Fingerprint, já importado acima).
using TxDbType = DoNetDrive.Protocol.Door.Door8800.Transaction.e_TransactionDatabaseType;
using ReadTxDbResult = DoNetDrive.Protocol.Door.Door8800.Transaction.ReadTransactionDatabase_Result;

namespace HospitalAccess.Gateway;

/// <summary>
/// Implementação concreta do gateway usando o SDK novo DoNetDrive.*.
/// ÚNICO componente que toca o hardware. Assinaturas confirmadas em TestClass.cs,
/// frmPerson.cs, frmDoor.cs e ConnectorAllocatorTestClass.cs do projeto de exemplo
/// DoNetDrive.Protocol.Fingerprint.Test, mais engenharia reversa (reflexão) do IL das
/// DLLs reais para o enum CommandDetailFactory.ConnectType (ver ControllerConnectionFactory).
/// </summary>
public sealed class DoNetDriveGateway : IDeviceGateway, IDisposable
{
    private readonly ControllerConnectionFactory _connections;
    private readonly ConnectorAllocator _allocator;
    private readonly TimeZoneInfo _deviceTimeZone;

    /// <summary>Controladores com monitoramento ativo, indexados por SN — usado para responder ao teste de conexão (0xA0).</summary>
    private readonly ConcurrentDictionary<string, Controller> _monitored = new();

    /// <summary>
    /// Um lock por controlador (Id): serializa TODOS os comandos ao MESMO aparelho (sync, health,
    /// porta, leitura...). Sem isto, dois comandos concorrentes no mesmo controlador estouravam com
    /// CommandStatus_Timeout (ex.: um AddPersonAndImage de face colidindo com o heartbeat do
    /// health-check). Comandos a controladores DIFERENTES continuam em paralelo.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _controllerGates = new();

    public DoNetDriveGateway(ControllerConnectionFactory connections, TimeZoneInfo deviceTimeZone)
    {
        _connections = connections;
        _deviceTimeZone = deviceTimeZone;
        _allocator = ConnectorAllocator.GetAllocator();
        _allocator.TransactionMessage += OnTransactionMessage;
    }

    /// <summary>
    /// Converte um horário UTC para o "relógio de parede" do fuso do aparelho. O campo de validade
    /// (Expiry) do controlador é BCD em horário LOCAL: o aparelho compara com o próprio relógio.
    /// Se enviarmos UTC, a validade fica adiantada (ex.: em UTC-3, o fim aparece 3h a mais no
    /// aparelho). Convertendo para o fuso do aparelho, a data enviada bate com o relógio dele.
    /// </summary>
    private DateTime ToDeviceWallClock(DateTime utc)
    {
        var asUtc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        return TimeZoneInfo.ConvertTimeFromUtc(asUtc, _deviceTimeZone);
    }

    public event EventHandler<DeviceAccessEvent>? AccessEventReceived;
    public event EventHandler<DeviceAlarmEvent>? AlarmEventReceived;

    // ----------------------------------------------------------------------------------
    // Execução verificada de comandos (antes: uma escrita que dava timeout passava como
    // sucesso silencioso). Agora todo comando confere o status final e lança em caso de
    // falha/cancelamento/timeout.
    // ----------------------------------------------------------------------------------

    private async Task RunAsync(INCommand cmd, string operation, Controller controller)
    {
        // Serializa por controlador: nunca dois comandos ao mesmo aparelho ao mesmo tempo. O lock é
        // mantido só durante o comando (que tem timeout próprio = cmdDtl.Timeout), então não trava
        // indefinidamente. Controladores diferentes rodam em paralelo (locks distintos).
        var gate = _controllerGates.GetOrAdd(controller.Id, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            try
            {
                await _allocator.AddCommandAsync(cmd);
            }
            catch (Exception ex)
            {
                throw new DeviceCommandException(
                    $"{operation} falhou no controlador '{controller.Name}' ({controller.IpAddress}:{controller.Port}): {ex.Message}", ex);
            }

            var status = cmd.GetStatus();
            if (status is null || status.IsFaulted || status.IsCanceled)
            {
                var reason = status?.IsCanceled == true ? "cancelado/timeout"
                    : status?.IsFaulted == true ? "falha reportada pelo controlador"
                    : "comando não confirmado";
                throw new DeviceCommandException(
                    $"{operation} não confirmado pelo controlador '{controller.Name}' ({controller.IpAddress}:{controller.Port}): {reason}.");
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<TResult> RunAsync<TResult>(INCommand cmd, string operation, Controller controller) where TResult : class
    {
        await RunAsync(cmd, operation, controller);
        return cmd.getResult() as TResult
            ?? throw new DeviceCommandException(
                $"{operation} não retornou resultado do controlador '{controller.Name}' ({controller.IpAddress}:{controller.Port}).");
    }

    public async Task<string> ReadSerialNumberAsync(Controller controller, CancellationToken ct = default)
    {
        var cmd = new ReadSN(_connections.CreateCommandDetail(controller));
        var result = await RunAsync<SN_Result>(cmd, "ReadSN", controller);
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
            person.Expiry = ToDeviceWallClock(validUntil);
        if (user.CardNumber is { } cardNumber)
            person.CardData = cardNumber;

        // Feriado é default-deny no 8190H (protocolo §8.1 campo 14): a pessoa só passa num
        // feriado nº i se o bit i estiver ligado. Sem isso, qualquer feriado cadastrado barra
        // TODOS os funcionários (evento 18). Habilitamos os 30 slots de feriado que o sistema
        // gerencia para usuários permanentes; a restrição de feriado fica reservada a cenários
        // específicos, não é o default acidental de "bitmap zerado = bloqueado".
        if (user.Type == UserType.Permanent)
        {
            for (var holidayGroup = 1; holidayGroup <= 30; holidayGroup++)
                person.SetHolidayValue(holidayGroup, true);
        }

        var identification = new IdentificationData(1, converted); // File Type 1 = foto de pessoa
        var par = new AddPersonAndImage_Parameter(person, identification)
        {
            // WaitRepeatMessage só funciona em firmware >= v4.28 (aviso do README).
            WaitRepeatMessage = controller.SupportsWaitRepeatMessage,
        };

        var cmd = new AddPeosonAndImage(_connections.CreateCommandDetail(controller), par); // grafia confirmada no SDK
        await RunAsync(cmd, "AddPersonAndImage", controller);

        return MapAddFaceResult(cmd.getResult() as AddPersonAndImage_Result);
    }

    private static AddFaceResult MapAddFaceResult(AddPersonAndImage_Result? result)
    {
        if (result is null)
        {
            // Handle 0 no protocolo (Classe 11 / 0x3B 0x01) = usuário inexistente / recusado.
            return new AddFaceResult(false, FaceUploadCode.UserNotFound,
                "Controlador recusou a operação (handle 0 / usuário inexistente).");
        }

        // Falha ao gravar o cadastro da PESSOA (perfil) é distinta da foto — não mascarar como
        // problema de imagem.
        if (!result.UserUploadStatus)
        {
            return new AddFaceResult(false, FaceUploadCode.UserNotFound,
                "Controlador não gravou o cadastro da pessoa (perfil recusado).");
        }

        // A lista de status da imagem pode vir vazia (nenhum dado adicional aceito).
        if (result.IdDataUploadStatus is not { Count: > 0 })
        {
            return new AddFaceResult(false, FaceUploadCode.CrcFailure,
                "Controlador não retornou status do upload da foto/feature code.");
        }

        var status = result.IdDataUploadStatus[0];
        var repeatUser = result.IdDataRepeatUser is { Count: > 0 } ? result.IdDataRepeatUser[0] : 0;
        return status switch
        {
            1 => new AddFaceResult(true, FaceUploadCode.Ok, null),
            2 => new AddFaceResult(false, FaceUploadCode.FeatureCodeUnidentifiable,
                "Feature code não identificável na foto."),
            3 => new AddFaceResult(false, FaceUploadCode.NoFaceInPhoto,
                "Nenhum rosto reconhecível na foto."),
            4 => new AddFaceResult(false, FaceUploadCode.Duplicate,
                $"Foto/feature code duplicado (usuário existente: {repeatUser}).", repeatUser),
            _ => new AddFaceResult(false, FaceUploadCode.CrcFailure,
                $"Falha de verificação CRC32 no upload (status retornado: {status})."),
        };
    }

    /// <summary>
    /// Cadastra/atualiza uma pessoa SEM biometria facial — usado para visitantes, que são
    /// identificados apenas pelo QR (o leitor lê o QR como "cartão" e valida contra a pessoa
    /// cadastrada). A validade nativa do controlador (Person.Expiry) e o grupo de horário são
    /// gravados, de modo que o próprio dispositivo bloqueia o acesso após o vencimento — a gestão
    /// (criar/remover) é feita pelo sistema. Protocolo §8.1 (campos Validade e TimeGroup).
    /// </summary>
    public async Task AddPersonWithoutFaceAsync(Controller controller, User user, CancellationToken ct = default)
    {
        var person = new PersonData
        {
            UserCode = user.UserCode,
            PName = user.Name,
            TimeGroup = user.TimeGroup,
        };
        if (user.ValidUntil is { } validUntil)
            person.Expiry = ToDeviceWallClock(validUntil);
        if (user.CardNumber is { } cardNumber)
            person.CardData = cardNumber;

        var par = new AddPerson_Parameter(new List<PersonData> { person });
        var cmd = new AddPerson(_connections.CreateCommandDetail(controller), par);
        await RunAsync(cmd, "AddPerson", controller);

        if (cmd.getResult() is WritePerson_Result { FailTotal: > 0 })
            throw new DeviceCommandException(
                $"AddPerson não gravou a pessoa {user.UserCode} no controlador '{controller.Name}'.");
    }

    public async Task DeletePersonAsync(Controller controller, uint userCode, CancellationToken ct = default)
    {
        var person = new PersonData { UserCode = userCode };
        var par = new DeletePerson_Parameter(new List<PersonData> { person });
        var cmd = new DeletePerson(_connections.CreateCommandDetail(controller), par);
        await RunAsync(cmd, "DeletePerson", controller);
    }

    /// <summary>
    /// Apaga TODAS as pessoas cadastradas no controlador (protocolo §8.2, 0x07 0x02). Usado no
    /// "resincronizar forçado", que limpa o dispositivo e reenvia os cadastros do sistema.
    /// </summary>
    public async Task ClearAllPersonsAsync(Controller controller, CancellationToken ct = default)
    {
        var cmdDtl = _connections.CreateCommandDetail(controller);
        cmdDtl.Timeout = Math.Max(controller.TimeoutMs, 15000); // apagar a base pode demorar
        var cmd = new ClearPersonDataBase(cmdDtl);
        await RunAsync(cmd, "ClearPersonDataBase", controller);
    }

    /// <summary>
    /// Dispara o alarme de incêndio no controlador (protocolo §5, 0x04 0x01 0x00). Usado no
    /// acionamento de emergência/evacuação.
    /// </summary>
    public async Task TriggerFireAlarmAsync(Controller controller, CancellationToken ct = default)
    {
        var cmd = new AlarmNs.SendFireAlarm.WriteSendFireAlarm(_connections.CreateCommandDetail(controller));
        await RunAsync(cmd, "SendFireAlarm", controller);
    }

    /// <summary>
    /// Drena os registros de autenticação armazenados no controlador (Classe VIII) que ainda não
    /// foram coletados, avançando o ponteiro de leitura (AutoWriteReadIndex) — defesa em
    /// profundidade para não perder eventos ocorridos com o servidor fora do ar. Retorna os
    /// eventos normalizados; a deduplicação (por SN + nº de série do registro) fica a cargo de
    /// quem persiste. Não validado contra hardware real.
    /// </summary>
    public async Task<IReadOnlyList<DeviceAccessEvent>> CollectAccessRecordsAsync(Controller controller, CancellationToken ct = default)
    {
        var events = new List<DeviceAccessEvent>();

        // Guarda-limite para não girar indefinidamente se o firmware não zerar 'readable'.
        for (var round = 0; round < 1000 && !ct.IsCancellationRequested; round++)
        {
            var cmdDtl = _connections.CreateCommandDetail(controller);
            cmdDtl.Timeout = Math.Max(controller.TimeoutMs, 15000);
            var par = new ReadTransactionDatabase_Parameter((int)TxDbType.OnCardTransaction, 50)
            {
                AutoWriteReadIndex = true, // avança o ponteiro de 'não lidos'
            };
            var cmd = new ReadTransactionDatabase(cmdDtl, par);
            var result = await RunAsync<ReadTxDbResult>(cmd, "ReadTransactionDatabase", controller);

            var count = 0;
            foreach (var record in result.TransactionList)
            {
                if (record is not CardTransaction card) continue;
                events.Add(BuildAccessEvent(controller.SerialNumber, card));
                count++;
            }

            if (result.readable <= 0 || count == 0) break;
        }

        return events;
    }

    public async Task OpenDoorAsync(Controller controller, CancellationToken ct = default)
    {
        // O 8190H (A33_Face) tem um único relé de porta por controlador; o comando de
        // desbloqueio remoto (Classe 3 / Remote Unlock) não recebe índice de relé.
        var cmd = new OpenDoor(_connections.CreateCommandDetail(controller));
        await RunAsync(cmd, "OpenDoor", controller);
    }

    public async Task CloseDoorAsync(Controller controller, CancellationToken ct = default)
    {
        var cmd = new CloseDoor(_connections.CreateCommandDetail(controller));
        await RunAsync(cmd, "CloseDoor", controller);
    }

    public async Task HoldDoorOpenAsync(Controller controller, CancellationToken ct = default)
    {
        var cmd = new HoldDoor(_connections.CreateCommandDetail(controller));
        await RunAsync(cmd, "HoldDoor", controller);
    }

    public async Task LockDoorAsync(Controller controller, CancellationToken ct = default)
    {
        var cmd = new LockDoor(_connections.CreateCommandDetail(controller));
        await RunAsync(cmd, "LockDoor", controller);
    }

    public async Task UnlockDoorAsync(Controller controller, CancellationToken ct = default)
    {
        var cmd = new UnlockDoor(_connections.CreateCommandDetail(controller));
        await RunAsync(cmd, "UnlockDoor", controller);
    }

    public async Task<ControllerNetworkInfo> ReadNetworkSettingsAsync(Controller controller, CancellationToken ct = default)
    {
        var cmd = new ReadTCPSetting(_connections.CreateCommandDetail(controller), false);
        var result = await RunAsync<ReadTCPSetting_Result>(cmd, "ReadTCPSetting", controller);

        var tcp = result.TCP;
        return new ControllerNetworkInfo(tcp.mMAC, tcp.mIP, tcp.mIPMask, tcp.mIPGateway, tcp.mDNS, tcp.mDNSBackup,
            tcp.mUDPPort, tcp.mServerIP, tcp.mServerAddr, tcp.mServerPort, tcp.mAutoIP);
    }

    public async Task WriteNetworkSettingsAsync(Controller controller, ControllerNetworkInfo info, CancellationToken ct = default)
    {
        var tcp = new TCPDetail
        {
            mMAC = info.Mac,
            mIP = info.Ip,
            mIPMask = info.IpMask,
            mIPGateway = info.IpGateway,
            mDNS = info.Dns,
            mDNSBackup = info.DnsBackup,
            mTCPPort = controller.Port,
            mUDPPort = info.UdpPort,
            mServerIP = info.ServerIp,
            mServerAddr = info.ServerAddr,
            mServerPort = info.ServerPort,
            mProtocolType = 1,
            mAutoIP = info.AutoIp,
        };

        var par = new WriteTCPSetting_Parameter(tcp) { UDPBroadcast = false };
        var cmd = new WriteTCPSetting(_connections.CreateCommandDetail(controller), par);
        await RunAsync(cmd, "WriteTCPSetting", controller);
    }

    /// <summary>
    /// Varredura por broadcast UDP (SN "0000000000000000" / senha "FFFFFFFF", valores especiais
    /// do SDK — ver ControllerConnectionFactory.CreateBroadcastDetail). Cada controlador que
    /// responde dispara CommandCompleteEvent separadamente; coletamos durante a janela de tempo.
    /// Não validado contra hardware real.
    /// </summary>
    public async Task<IReadOnlyList<DiscoveredController>> DiscoverControllersAsync(int udpPort, TimeSpan scanDuration, CancellationToken ct = default)
    {
        var found = new List<DiscoveredController>();
        var cmdDtl = _connections.CreateBroadcastDetail(udpPort);

        // Sem bind UDP local prévio a resposta em broadcast dos dispositivos não é recebida.
        _allocator.OpenForciblyConnect(cmdDtl.Connector);

        void OnComplete(object? sender, CommandEventArgs e)
        {
            if (e.Result is not SearchControltor_Result result) return;
            if (string.IsNullOrWhiteSpace(result.SN) || result.SN.Length != 16) return;

            lock (found)
            {
                if (!found.Exists(f => f.SerialNumber == result.SN))
                    found.Add(new DiscoveredController(result.SN, result.TCP?.mIP ?? string.Empty));
            }
        }

        cmdDtl.CommandCompleteEvent += OnComplete;
        try
        {
            var searchId = (ushort)Random.Shared.Next(1, ushort.MaxValue);
            // UDPBroadcast=true é obrigatório: sem ele o frame não é enviado como broadcast
            // (o default do parâmetro é false). O demo oficial seta este flag na busca.
            var par = new SearchControltor_Parameter(searchId) { UDPBroadcast = true };
            var cmd = new SearchControltor(cmdDtl, par);
            var fireAndForget = _allocator.AddCommandAsync(cmd);
            _ = fireAndForget.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            await Task.Delay(scanDuration, ct);
        }
        finally
        {
            cmdDtl.CommandCompleteEvent -= OnComplete;
        }

        lock (found) { return found.ToList(); }
    }

    public async Task<DateTime> ReadControllerTimeAsync(Controller controller, CancellationToken ct = default)
    {
        var cmd = new ReadTime(_connections.CreateCommandDetail(controller));
        var result = await RunAsync<ReadTime_Result>(cmd, "ReadTime", controller);
        return result.ControllerDate;
    }

    public async Task SyncControllerTimeAsync(Controller controller, CancellationToken ct = default)
    {
        var cmd = new WriteTime(_connections.CreateCommandDetail(controller)); // grava o horário atual deste servidor
        await RunAsync(cmd, "WriteTime", controller);
    }

    public async Task SyncHolidaysAsync(Controller controller, IReadOnlyList<HolidayEntry> holidays, CancellationToken ct = default)
    {
        await RunAsync(new ClearHoliday(_connections.CreateCommandDetail(controller)), "ClearHoliday", controller);

        if (holidays.Count == 0) return;

        var list = holidays.Select(h => new HolidayDetail
        {
            Index = h.Index,
            HolidayType = h.HolidayType,
            Holiday = h.RepeatsYearly ? new DateTime(2000, h.Date.Month, h.Date.Day) : h.Date,
        }).ToList();

        var par = new AddHoliday_Parameter(list);
        var cmd = new AddHoliday(_connections.CreateCommandDetail(controller), par);
        await RunAsync(cmd, "AddHoliday", controller);
    }

    /// <summary>
    /// Grava os 64 grupos de horário de uma vez (AddTimeGroup é uma substituição completa, não
    /// incremental — grupos sem definição na nossa base ficam "sempre fechado" naquele dia).
    /// </summary>
    public async Task SyncTimeGroupsAsync(Controller controller, IReadOnlyList<TimeGroupEntry> groups, CancellationToken ct = default)
    {
        var byGroupNumber = groups.ToDictionary(g => g.GroupNumber);
        var weekGroups = new List<DataTimeGroup.WeekTimeGroup>(64);

        for (var groupNumber = 1; groupNumber <= 64; groupNumber++)
        {
            var week = new DataTimeGroup.WeekTimeGroup(8);
            if (byGroupNumber.TryGetValue(groupNumber, out var entry))
            {
                foreach (var seg in entry.Segments)
                {
                    var day = week.GetItem(seg.Weekday);
                    var segment = day.GetItem(seg.SegmentIndex);
                    segment.SetBeginTime(seg.Begin.Hour, seg.Begin.Minute);
                    segment.SetEndTime(seg.End.Hour, seg.End.Minute);
                }
            }
            weekGroups.Add(week);
        }

        var par = new AddTimeGroup_Parameter(weekGroups);
        var cmd = new AddTimeGroup(_connections.CreateCommandDetail(controller), par);
        await RunAsync(cmd, "AddTimeGroup", controller);
    }

    public async Task<AlarmSettingsSnapshot> ReadAlarmSettingsAsync(Controller controller, CancellationToken ct = default)
    {
        var blacklistCmd = new AlarmNs.BlacklistAlarm.ReadBlacklistAlarm(_connections.CreateCommandDetail(controller));
        var blacklist = await RunAsync<AlarmNs.BlacklistAlarm.ReadBlacklistAlarm_Result>(blacklistCmd, "ReadBlacklistAlarm", controller);

        var tamperCmd = new AlarmNs.AntiDisassemblyAlarm.ReadAntiDisassemblyAlarm(_connections.CreateCommandDetail(controller));
        var tamper = await RunAsync<AlarmNs.AntiDisassemblyAlarm.ReadAntiDisassemblyAlarm_Result>(tamperCmd, "ReadAntiDisassemblyAlarm", controller);

        var illegalCmd = new AlarmNs.IllegalVerificationAlarm.ReadIllegalVerificationAlarm(_connections.CreateCommandDetail(controller));
        var illegal = await RunAsync<AlarmNs.IllegalVerificationAlarm.ReadIllegalVerificationAlarm_Result>(illegalCmd, "ReadIllegalVerificationAlarm", controller);

        var duressCmd = new AlarmNs.AlarmPassword.ReadAlarmPassword(_connections.CreateCommandDetail(controller));
        var duress = await RunAsync<AlarmNs.AlarmPassword.ReadAlarmPassword_Result>(duressCmd, "ReadAlarmPassword", controller);

        var timeoutCmd = new AlarmNs.OpenDoorTimeoutAlarm.ReadOpenDoorTimeoutAlarm(_connections.CreateCommandDetail(controller));
        var timeout = await RunAsync<AlarmNs.OpenDoorTimeoutAlarm.ReadOpenDoorTimeoutAlarm_Result>(timeoutCmd, "ReadOpenDoorTimeoutAlarm", controller);

        var legalCmd = new AlarmNs.LegalVerificationCloseAlarm.ReadLegalVerificationCloseAlarm(_connections.CreateCommandDetail(controller));
        var legal = await RunAsync<AlarmNs.LegalVerificationCloseAlarm.ReadLegalVerificationCloseAlarm_Result>(legalCmd, "ReadLegalVerificationCloseAlarm", controller);

        return new AlarmSettingsSnapshot(
            FireAlarmEnabled: false, // SDK não expõe leitura do estado do alarme de incêndio, só escrita (SetFireAlarm).
            BlacklistAlarmEnabled: blacklist.IsAlarm,
            TamperAlarmEnabled: tamper.IsUse,
            IllegalVerificationAlarmEnabled: illegal.IsUse,
            IllegalVerificationTimes: illegal.Times,
            DuressAlarmEnabled: duress.Use,
            DuressPassword: duress.Password,
            OpenDoorTimeoutAlarmEnabled: timeout.IsUse,
            OpenDoorTimeoutSeconds: timeout.AllowTime,
            LegalVerificationCloseAlarmEnabled: legal.IsUse,
            DuressMode: duress.AlarmOption == 0 ? 1 : duress.AlarmOption);
    }

    public async Task WriteAlarmSettingsAsync(Controller controller, AlarmSettingsSnapshot settings, CancellationToken ct = default)
    {
        await RunAsync(new AlarmNs.SetFireAlarm(_connections.CreateCommandDetail(controller), settings.FireAlarmEnabled),
            "SetFireAlarm", controller);

        await RunAsync(new AlarmNs.BlacklistAlarm.WriteBlacklistAlarm(
            _connections.CreateCommandDetail(controller),
            new AlarmNs.BlacklistAlarm.WriteBlacklistAlarm_Parameter(settings.BlacklistAlarmEnabled)),
            "WriteBlacklistAlarm", controller);

        await RunAsync(new AlarmNs.AntiDisassemblyAlarm.WriteAntiDisassemblyAlarm(
            _connections.CreateCommandDetail(controller),
            new AlarmNs.AntiDisassemblyAlarm.WriteAntiDisassemblyAlarm_Parameter(settings.TamperAlarmEnabled)),
            "WriteAntiDisassemblyAlarm", controller);

        await RunAsync(new AlarmNs.IllegalVerificationAlarm.WriteIllegalVerificationAlarm(
            _connections.CreateCommandDetail(controller),
            new AlarmNs.IllegalVerificationAlarm.WriteIllegalVerificationAlarm_Parameter(
                settings.IllegalVerificationAlarmEnabled, settings.IllegalVerificationTimes)),
            "WriteIllegalVerificationAlarm", controller);

        // Preserva o modo de coação lido (AlarmOption) em vez de fixá-lo em 1.
        var duressMode = settings.DuressMode is >= 1 and <= 3 ? settings.DuressMode : 1;
        await RunAsync(new AlarmNs.AlarmPassword.WriteAlarmPassword(
            _connections.CreateCommandDetail(controller),
            new AlarmNs.AlarmPassword.WriteAlarmPassword_Parameter(
                settings.DuressAlarmEnabled, settings.DuressPassword ?? string.Empty, duressMode)),
            "WriteAlarmPassword", controller);

        // O protocolo aceita até 65535 s de timeout de porta (AllowTime é ushort) — não clampar a 255.
        var allowTime = (ushort)Math.Clamp(settings.OpenDoorTimeoutSeconds, 0, ushort.MaxValue);
        await RunAsync(new AlarmNs.OpenDoorTimeoutAlarm.WriteOpenDoorTimeoutAlarm(
            _connections.CreateCommandDetail(controller),
            new AlarmNs.OpenDoorTimeoutAlarm.WriteOpenDoorTimeoutAlarm_Parameter(
                settings.OpenDoorTimeoutAlarmEnabled, allowTime, false)),
            "WriteOpenDoorTimeoutAlarm", controller);

        await RunAsync(new AlarmNs.LegalVerificationCloseAlarm.WriteLegalVerificationCloseAlarm(
            _connections.CreateCommandDetail(controller),
            new AlarmNs.LegalVerificationCloseAlarm.WriteLegalVerificationCloseAlarm_Parameter(settings.LegalVerificationCloseAlarmEnabled)),
            "WriteLegalVerificationCloseAlarm", controller);
    }

    /// <summary>Fecha os 7 tipos de alarme configuráveis do controlador (bitmap fixo do SDK — ver frmAlarm.cs).</summary>
    public async Task ClearAlarmAsync(Controller controller, CancellationToken ct = default)
    {
        var par = new AlarmNs.CloseAlarm_Parameter([1, 1, 1, 1, 1, 1, 1]);
        var cmd = new AlarmNs.CloseAlarm(_connections.CreateCommandDetail(controller), par);
        await RunAsync(cmd, "CloseAlarm", controller);
    }

    /// <summary>
    /// Baixa as fotos mais recentes de eventos (Classe XI). O SDK grava os arquivos em disco e
    /// os expõe no próprio registro (CardAndImageTransaction.PhotoFile / .PhotoDataBuf) — usamos
    /// esse vínculo direto por registro, em vez de parear por ordem cronológica de arquivo (que
    /// atribui a foto à pessoa errada quando algum registro não tem foto). Registros sem foto
    /// (Photo == 0) são ignorados. AutoWriteReadIndex=false: esta é uma consulta de conveniência,
    /// não deve avançar o ponteiro de "não lidos" e roubar eventos do coletor principal.
    /// </summary>
    public async Task<IReadOnlyList<CapturedEventPhoto>> ReadRecentEventPhotosAsync(Controller controller, int quantity, CancellationToken ct = default)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "hospitalaccess-event-photos", controller.SerialNumber);
        Directory.CreateDirectory(tempDir);

        var cmdDtl = _connections.CreateCommandDetail(controller);
        cmdDtl.Timeout = Math.Max(controller.TimeoutMs, 30000);
        var par = new ReadTransactionAndImageDatabase_Parameter(quantity, true, tempDir)
        {
            AutoWriteReadIndex = false, // não avançar o ponteiro de leitura do coletor principal
        };
        var cmd = new ReadTransactionAndImageDatabase(cmdDtl, par);

        try
        {
            var result = await RunAsync<ReadTransactionAndImageDatabase_Result>(cmd, "ReadTransactionAndImageDatabase", controller);

            var photos = new List<CapturedEventPhoto>();
            foreach (var record in result.TransactionList)
            {
                if (record is not CardAndImageTransaction img) continue;
                if (img.Photo == 0) continue; // registro sem foto (ex.: abertura por botão)

                var bytes = await LoadPhotoBytesAsync(img, ct);
                if (bytes is null || bytes.Length == 0) continue;

                photos.Add(new CapturedEventPhoto(
                    img.UserCode,
                    img.TransactionDate.ToUniversalTime(),
                    img.TransactionCode,
                    bytes));
            }

            return photos;
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* melhor esforço */ }
        }
    }

    private static async Task<byte[]?> LoadPhotoBytesAsync(CardAndImageTransaction img, CancellationToken ct)
    {
        if (img.PhotoDataBuf is { Length: > 0 }) return img.PhotoDataBuf;
        if (!string.IsNullOrEmpty(img.PhotoFile) && File.Exists(img.PhotoFile))
            return await File.ReadAllBytesAsync(img.PhotoFile, ct);
        return null;
    }

    public async Task<IReadOnlyList<uint>> ReadRegisteredUserCodesAsync(Controller controller, CancellationToken ct = default)
    {
        var cmdDtl = _connections.CreateCommandDetail(controller);
        cmdDtl.Timeout = Math.Max(controller.TimeoutMs, 15000); // banco de pessoas pode ser grande
        var cmd = new ReadPersonDataBase(cmdDtl);
        var result = await RunAsync<ReadPersonDataBase_Result>(cmd, "ReadPersonDataBase", controller);
        return result.PersonList.Select(p => p.UserCode).ToList();
    }

    public async Task<KioskSettingsSnapshot> ReadKioskSettingsAsync(Controller controller, CancellationToken ct = default)
    {
        var language = await RunAsync<ReadDriveLanguage_Result>(
            new ReadDriveLanguage(_connections.CreateCommandDetail(controller)), "ReadDriveLanguage", controller);
        var volume = await RunAsync<ReadDriveVolume_Result>(
            new ReadDriveVolume(_connections.CreateCommandDetail(controller)), "ReadDriveVolume", controller);
        var led = await RunAsync<ReadFaceLEDMode_Result>(
            new ReadFaceLEDMode(_connections.CreateCommandDetail(controller)), "ReadFaceLEDMode", controller);
        var mask = await RunAsync<ReadFaceMouthmufflePar_Result>(
            new ReadFaceMouthmufflePar(_connections.CreateCommandDetail(controller)), "ReadFaceMouthmufflePar", controller);
        var temp = await RunAsync<ReadFaceBodyTemperaturePar_Result>(
            new ReadFaceBodyTemperaturePar(_connections.CreateCommandDetail(controller)), "ReadFaceBodyTemperaturePar", controller);
        var tempAlarm = await RunAsync<ReadFaceBodyTemperatureAlarmPar_Result>(
            new ReadFaceBodyTemperatureAlarmPar(_connections.CreateCommandDetail(controller)), "ReadFaceBodyTemperatureAlarmPar", controller);
        var tempShow = await RunAsync<ReadFaceBodyTemperatureShowPar_Result>(
            new ReadFaceBodyTemperatureShowPar(_connections.CreateCommandDetail(controller)), "ReadFaceBodyTemperatureShowPar", controller);
        var range = await RunAsync<ReadFaceIdentifyRange_Result>(
            new ReadFaceIdentifyRange(_connections.CreateCommandDetail(controller)), "ReadFaceIdentifyRange", controller);
        var liveness = await RunAsync<ReadFaceBioassay_Result>(
            new ReadFaceBioassay(_connections.CreateCommandDetail(controller)), "ReadFaceBioassay", controller);
        var livenessSim = await RunAsync<ReadFaceBioassaySimilarity_Result>(
            new ReadFaceBioassaySimilarity(_connections.CreateCommandDetail(controller)), "ReadFaceBioassaySimilarity", controller);

        return new KioskSettingsSnapshot(
            Language: language.Language,
            Volume: volume.Volume,
            FillLightMode: led.LEDMode,
            MaskDetectionMode: mask.Mouthmuffle,
            TemperatureDetectionMode: temp.BodyTemperaturePar,
            TemperatureAlarmThresholdX10: tempAlarm.AlarmPar,
            TemperatureDisplayMode: tempShow.IsShow,
            FaceIdentifyRange: range.IdentifyRange,
            LivenessDetectionMode: liveness.BioassayType,
            LivenessSimilarity: livenessSim.Similarity);
    }

    public async Task WriteKioskSettingsAsync(Controller controller, KioskSettingsSnapshot settings, CancellationToken ct = default)
    {
        await RunAsync(new WriteDriveLanguage(
            _connections.CreateCommandDetail(controller), new WriteDriveLanguage_Parameter(settings.Language)), "WriteDriveLanguage", controller);
        await RunAsync(new WriteDriveVolume(
            _connections.CreateCommandDetail(controller), new WriteDriveVolume_Parameter(settings.Volume)), "WriteDriveVolume", controller);
        await RunAsync(new WriteFaceLEDMode(
            _connections.CreateCommandDetail(controller), new WriteFaceLEDMode_Parameter(settings.FillLightMode)), "WriteFaceLEDMode", controller);
        await RunAsync(new WriteFaceMouthmufflePar(
            _connections.CreateCommandDetail(controller), new WriteFaceMouthmufflePar_Parameter(settings.MaskDetectionMode)), "WriteFaceMouthmufflePar", controller);
        await RunAsync(new WriteFaceBodyTemperaturePar(
            _connections.CreateCommandDetail(controller), new WriteFaceBodyTemperaturePar_Parameter(settings.TemperatureDetectionMode)), "WriteFaceBodyTemperaturePar", controller);
        await RunAsync(new WriteFaceBodyTemperatureAlarmPar(
            _connections.CreateCommandDetail(controller), new WriteFaceBodyTemperatureAlarmPar_Parameter(settings.TemperatureAlarmThresholdX10)), "WriteFaceBodyTemperatureAlarmPar", controller);
        await RunAsync(new WriteFaceBodyTemperatureShowPar(
            _connections.CreateCommandDetail(controller), new WriteFaceBodyTemperatureShowPar_Parameter(settings.TemperatureDisplayMode)), "WriteFaceBodyTemperatureShowPar", controller);
        await RunAsync(new WriteFaceIdentifyRange(
            _connections.CreateCommandDetail(controller), new WriteFaceIdentifyRange_Parameter(settings.FaceIdentifyRange)), "WriteFaceIdentifyRange", controller);
        await RunAsync(new WriteFaceBioassay(
            _connections.CreateCommandDetail(controller), new WriteFaceBioassay_Parameter(settings.LivenessDetectionMode)), "WriteFaceBioassay", controller);
        await RunAsync(new WriteFaceBioassaySimilarity(
            _connections.CreateCommandDetail(controller), new WriteFaceBioassaySimilarity_Parameter(settings.LivenessSimilarity)), "WriteFaceBioassaySimilarity", controller);
    }

    // ----------------------------------------------------------------------------------
    // Monitoramento em tempo real (BeginWatch). Sem isto o dispositivo NÃO empurra eventos:
    // o push vem desligado de fábrica e não persiste após reboot (protocolo §10). Segue o
    // padrão do demo oficial (FrmMain.buWatch_Click): abre a conexão forçada, adiciona o
    // Door8800RequestHandle para decodificar o push e envia BeginWatch. Não validado contra
    // hardware real (ver README seção 2).
    // ----------------------------------------------------------------------------------

    public async Task StartMonitoringAsync(Controller controller, CancellationToken ct = default)
    {
        var cmdDtl = _connections.CreateCommandDetail(controller);

        _allocator.OpenForciblyConnect(cmdDtl.Connector);
        var connector = _allocator.GetConnector(cmdDtl.Connector);
        if (connector is not null)
        {
            connector.OpenForciblyConnect(); // mantém o canal aberto para o push
            connector.RemoveRequestHandle(typeof(Door8800RequestHandle));
            connector.AddRequestHandle(new Door8800RequestHandle(UnpooledByteBufferAllocator.Default, RequestHandleFactory));
        }

        _monitored[controller.SerialNumber] = controller;
        await RunAsync(new BeginWatch(cmdDtl), "BeginWatch", controller);
    }

    public async Task StopMonitoringAsync(Controller controller, CancellationToken ct = default)
    {
        _monitored.TryRemove(controller.SerialNumber, out _);
        var cmdDtl = _connections.CreateCommandDetail(controller);
        try
        {
            await RunAsync(new CloseWatch(cmdDtl), "CloseWatch", controller);
        }
        finally
        {
            var connector = _allocator.GetConnector(cmdDtl.Connector);
            connector?.CloseForciblyConnect();
        }
    }

    /// <summary>
    /// Fábrica de tipos de registro para o Door8800RequestHandle decodificar o push, idêntica ao
    /// demo oficial (FrmMain.RequestHandleFactory): cmdIndex 1-4 usam a tabela do facial, 0x22 é
    /// keep-alive e 0xA0 é o teste de conexão.
    /// </summary>
    private static AbstractTransaction? RequestHandleFactory(string sn, byte cmdIndex, byte cmdPar)
    {
        if (cmdIndex is >= 1 and <= 4)
            return ReadTransactionDatabaseByIndex.NewTransactionTable[cmdIndex]();
        if (cmdIndex == 0x22)
            return new DoNetDrive.Protocol.Door.Door8800.Data.Transaction.KeepaliveTransaction();
        if (cmdIndex == 0xA0)
            return new DoNetDrive.Protocol.Door.Door8800.Data.Transaction.ConnectMessageTransaction();
        return null;
    }

    /// <summary>
    /// Evento em tempo real empurrado pelo controlador. O push chega como Door8800Transaction
    /// (envelope com CmdIndex/SN); o registro concreto fica em <c>.EventData</c> — confirmado no
    /// demo oficial (ConnectorAllocatorTestClass/ FrmMain: <c>fcTrn.EventData</c>). CmdIndex:
    /// 1 = autenticação (CardTransaction), 2 = sensor de porta, 3 = log de sistema (inclui os
    /// alarmes, protocolo §9.4), 4 = temperatura, 0x22 = keep-alive, 0xA0 = teste de conexão.
    /// </summary>
    private void OnTransactionMessage(INConnectorDetail connectorDetail, INData eventData)
    {
        if (eventData is not Door8800Transaction transaction) return;

        // Mantém a conexão aberta ao receber push (padrão do demo).
        var connector = _allocator.GetConnector(connectorDetail);
        if (connector is not null && !connector.IsForciblyConnect())
            connector.OpenForciblyConnect();

        var inner = transaction.EventData;

        switch (transaction.CmdIndex)
        {
            case 1: // registro de autenticação (cartão/face/QR)
                if (inner is CardTransaction card)
                    EmitAccessEvent(transaction.SN, card);
                break;

            case 3: // log de sistema — inclui os alarmes (protocolo §9.4)
                if (inner is SystemTransaction system)
                    EmitAlarmFromSystemLog(transaction.SN, system);
                break;

            case 0xA0: // teste de conexão: responder com SendConnectTestResponse para o device nos considerar online
                RespondConnectTest(transaction.SN);
                break;

            // CmdIndex 2 (sensor de porta) e 4 (temperatura) e 0x22 (keep-alive) não geram
            // eventos de negócio hoje — o keep-alive já mantém a conexão viva acima.
        }
    }

    private void EmitAccessEvent(string serialNumber, CardTransaction card) =>
        AccessEventReceived?.Invoke(this, BuildAccessEvent(serialNumber, card));

    private static DeviceAccessEvent BuildAccessEvent(string serialNumber, CardTransaction card) => new()
    {
        ControllerSerialNumber = serialNumber,
        TimestampUtc = card.TransactionDate.ToUniversalTime(),
        UserCode = card.UserCode,
        RecordSerialNumber = card.RecordSerialNumber,
        Method = card.TransactionCode switch
        {
            3 => AccessMethod.Face,
            1 => AccessMethod.QrCode, // QR de visitante é apresentado ao leitor como "cartão"
            _ => AccessMethod.Card,
        },
        RawEventCode = card.TransactionCode,
        Direction = card.Accesstype,
        Granted = TransactionCodeClassifier.IsAccessGranted(card.TransactionCode),
    };

    private void EmitAlarmFromSystemLog(string serialNumber, SystemTransaction system)
    {
        var raw = system.TransactionCode;
        if (!TransactionCodeClassifier.TryMapSystemAlarm(raw, out var kind, out var cleared))
            return; // outros logs de sistema (unlock por software, energia etc.) não são alarmes

        AlarmEventReceived?.Invoke(this, new DeviceAlarmEvent
        {
            ControllerSerialNumber = serialNumber,
            TimestampUtc = system.TransactionDate.ToUniversalTime(),
            Kind = kind,
            RawEventCode = raw,
            Cleared = cleared,
        });
    }

    private void RespondConnectTest(string serialNumber)
    {
        if (!_monitored.TryGetValue(serialNumber, out var controller)) return;
        var cmd = new SendConnectTestResponse(_connections.CreateCommandDetail(controller));
        // fire-and-forget: não bloquear a thread de IO do push
        _ = _allocator.AddCommandAsync(cmd)
            .ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
    }

    public void Dispose()
    {
        _allocator.TransactionMessage -= OnTransactionMessage;
        _allocator.Dispose();
    }
}
