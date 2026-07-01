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
using DoNetDrive.Protocol.Fingerprint.Transaction;
using HospitalAccess.Domain.Entities;
using HospitalAccess.Domain.Enums;
using HospitalAccess.Gateway.Connections;
using HospitalAccess.Gateway.Imaging;
using PersonData = DoNetDrive.Protocol.Fingerprint.Data.Person;
using DataTimeGroup = DoNetDrive.Protocol.Door.Door8800.Data.TimeGroup;
using AlarmNs = DoNetDrive.Protocol.Fingerprint.Alarm;

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
    public event EventHandler<DeviceAlarmEvent>? AlarmEventReceived;

    /// <summary>Códigos de AlarmTransaction.TransactionCode que representam cancelamento/encerramento (não disparo).</summary>
    private static readonly HashSet<int> ClearedAlarmCodes =
        new() { 0x11, 0x12, 0x13, 0x14, 0x15, 0x17, 0x18, 0x19, 0x1A, 0x21, 0x22, 0x23, 0x24, 0x25, 0x27, 0x28, 0x29, 0x2A };

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
        if (user.CardNumber is { } cardNumber)
            person.CardData = cardNumber;

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

    public async Task<ControllerNetworkInfo> ReadNetworkSettingsAsync(Controller controller, CancellationToken ct = default)
    {
        var cmdDtl = _connections.CreateCommandDetail(controller);
        var cmd = new ReadTCPSetting(cmdDtl, false);
        await _allocator.AddCommandAsync(cmd);

        var result = cmd.getResult() as ReadTCPSetting_Result
            ?? throw new InvalidOperationException($"ReadTCPSetting não retornou resultado para '{controller.Name}'.");

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

        var cmdDtl = _connections.CreateCommandDetail(controller);
        var par = new WriteTCPSetting_Parameter(tcp) { UDPBroadcast = false };
        var cmd = new WriteTCPSetting(cmdDtl, par);
        await _allocator.AddCommandAsync(cmd);
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
            var cmd = new SearchControltor(cmdDtl, new SearchControltor_Parameter(searchId));
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
        var cmdDtl = _connections.CreateCommandDetail(controller);
        var cmd = new ReadTime(cmdDtl);
        await _allocator.AddCommandAsync(cmd);

        var result = cmd.getResult() as ReadTime_Result
            ?? throw new InvalidOperationException($"ReadTime não retornou resultado para '{controller.Name}'.");
        return result.ControllerDate;
    }

    public async Task SyncControllerTimeAsync(Controller controller, CancellationToken ct = default)
    {
        var cmdDtl = _connections.CreateCommandDetail(controller);
        var cmd = new WriteTime(cmdDtl); // sem parâmetro: grava o horário atual deste servidor
        await _allocator.AddCommandAsync(cmd);
    }

    public async Task SyncHolidaysAsync(Controller controller, IReadOnlyList<HolidayEntry> holidays, CancellationToken ct = default)
    {
        var clearCmdDtl = _connections.CreateCommandDetail(controller);
        await _allocator.AddCommandAsync(new ClearHoliday(clearCmdDtl));

        if (holidays.Count == 0) return;

        var list = holidays.Select(h => new HolidayDetail
        {
            Index = h.Index,
            HolidayType = h.HolidayType,
            Holiday = h.RepeatsYearly ? new DateTime(2000, h.Date.Month, h.Date.Day) : h.Date,
        }).ToList();

        var cmdDtl = _connections.CreateCommandDetail(controller);
        var par = new AddHoliday_Parameter(list);
        var cmd = new AddHoliday(cmdDtl, par);
        await _allocator.AddCommandAsync(cmd);
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

        var cmdDtl = _connections.CreateCommandDetail(controller);
        var par = new AddTimeGroup_Parameter(weekGroups);
        var cmd = new AddTimeGroup(cmdDtl, par);
        await _allocator.AddCommandAsync(cmd);
    }

    public async Task<AlarmSettingsSnapshot> ReadAlarmSettingsAsync(Controller controller, CancellationToken ct = default)
    {
        var blacklistCmd = new AlarmNs.BlacklistAlarm.ReadBlacklistAlarm(_connections.CreateCommandDetail(controller));
        await _allocator.AddCommandAsync(blacklistCmd);
        var blacklist = blacklistCmd.getResult() as AlarmNs.BlacklistAlarm.ReadBlacklistAlarm_Result;

        var tamperCmd = new AlarmNs.AntiDisassemblyAlarm.ReadAntiDisassemblyAlarm(_connections.CreateCommandDetail(controller));
        await _allocator.AddCommandAsync(tamperCmd);
        var tamper = tamperCmd.getResult() as AlarmNs.AntiDisassemblyAlarm.ReadAntiDisassemblyAlarm_Result;

        var illegalCmd = new AlarmNs.IllegalVerificationAlarm.ReadIllegalVerificationAlarm(_connections.CreateCommandDetail(controller));
        await _allocator.AddCommandAsync(illegalCmd);
        var illegal = illegalCmd.getResult() as AlarmNs.IllegalVerificationAlarm.ReadIllegalVerificationAlarm_Result;

        var duressCmd = new AlarmNs.AlarmPassword.ReadAlarmPassword(_connections.CreateCommandDetail(controller));
        await _allocator.AddCommandAsync(duressCmd);
        var duress = duressCmd.getResult() as AlarmNs.AlarmPassword.ReadAlarmPassword_Result;

        var timeoutCmd = new AlarmNs.OpenDoorTimeoutAlarm.ReadOpenDoorTimeoutAlarm(_connections.CreateCommandDetail(controller));
        await _allocator.AddCommandAsync(timeoutCmd);
        var timeout = timeoutCmd.getResult() as AlarmNs.OpenDoorTimeoutAlarm.ReadOpenDoorTimeoutAlarm_Result;

        var legalCmd = new AlarmNs.LegalVerificationCloseAlarm.ReadLegalVerificationCloseAlarm(_connections.CreateCommandDetail(controller));
        await _allocator.AddCommandAsync(legalCmd);
        var legal = legalCmd.getResult() as AlarmNs.LegalVerificationCloseAlarm.ReadLegalVerificationCloseAlarm_Result;

        return new AlarmSettingsSnapshot(
            FireAlarmEnabled: false, // SDK não expõe leitura do estado do alarme de incêndio, só escrita (SetFireAlarm).
            BlacklistAlarmEnabled: blacklist?.IsAlarm ?? false,
            TamperAlarmEnabled: tamper?.IsUse ?? false,
            IllegalVerificationAlarmEnabled: illegal?.IsUse ?? false,
            IllegalVerificationTimes: illegal?.Times ?? 3,
            DuressAlarmEnabled: duress?.Use ?? false,
            DuressPassword: duress?.Password,
            OpenDoorTimeoutAlarmEnabled: timeout?.IsUse ?? false,
            OpenDoorTimeoutSeconds: timeout?.AllowTime ?? 30,
            LegalVerificationCloseAlarmEnabled: legal?.IsUse ?? false);
    }

    public async Task WriteAlarmSettingsAsync(Controller controller, AlarmSettingsSnapshot settings, CancellationToken ct = default)
    {
        await _allocator.AddCommandAsync(new AlarmNs.SetFireAlarm(_connections.CreateCommandDetail(controller), settings.FireAlarmEnabled));

        await _allocator.AddCommandAsync(new AlarmNs.BlacklistAlarm.WriteBlacklistAlarm(
            _connections.CreateCommandDetail(controller),
            new AlarmNs.BlacklistAlarm.WriteBlacklistAlarm_Parameter(settings.BlacklistAlarmEnabled)));

        await _allocator.AddCommandAsync(new AlarmNs.AntiDisassemblyAlarm.WriteAntiDisassemblyAlarm(
            _connections.CreateCommandDetail(controller),
            new AlarmNs.AntiDisassemblyAlarm.WriteAntiDisassemblyAlarm_Parameter(settings.TamperAlarmEnabled)));

        await _allocator.AddCommandAsync(new AlarmNs.IllegalVerificationAlarm.WriteIllegalVerificationAlarm(
            _connections.CreateCommandDetail(controller),
            new AlarmNs.IllegalVerificationAlarm.WriteIllegalVerificationAlarm_Parameter(
                settings.IllegalVerificationAlarmEnabled, settings.IllegalVerificationTimes)));

        await _allocator.AddCommandAsync(new AlarmNs.AlarmPassword.WriteAlarmPassword(
            _connections.CreateCommandDetail(controller),
            new AlarmNs.AlarmPassword.WriteAlarmPassword_Parameter(
                settings.DuressAlarmEnabled, settings.DuressPassword ?? string.Empty, 1)));

        await _allocator.AddCommandAsync(new AlarmNs.OpenDoorTimeoutAlarm.WriteOpenDoorTimeoutAlarm(
            _connections.CreateCommandDetail(controller),
            new AlarmNs.OpenDoorTimeoutAlarm.WriteOpenDoorTimeoutAlarm_Parameter(
                settings.OpenDoorTimeoutAlarmEnabled, (ushort)Math.Clamp(settings.OpenDoorTimeoutSeconds, 0, 255), false)));

        await _allocator.AddCommandAsync(new AlarmNs.LegalVerificationCloseAlarm.WriteLegalVerificationCloseAlarm(
            _connections.CreateCommandDetail(controller),
            new AlarmNs.LegalVerificationCloseAlarm.WriteLegalVerificationCloseAlarm_Parameter(settings.LegalVerificationCloseAlarmEnabled)));
    }

    /// <summary>Fecha os 7 tipos de alarme configuráveis do controlador (bitmap fixo do SDK — ver frmAlarm.cs).</summary>
    public async Task ClearAlarmAsync(Controller controller, CancellationToken ct = default)
    {
        var cmdDtl = _connections.CreateCommandDetail(controller);
        var par = new AlarmNs.CloseAlarm_Parameter([1, 1, 1, 1, 1, 1, 1]);
        var cmd = new AlarmNs.CloseAlarm(cmdDtl, par);
        await _allocator.AddCommandAsync(cmd);
    }

    /// <summary>
    /// As fotos são gravadas pelo próprio SDK em disco (não vêm em memória) — usamos uma pasta
    /// temporária por controlador e associamos os arquivos aos registros pela ordem cronológica,
    /// já que o nome de arquivo gerado pelo SDK não é documentado no material de referência.
    /// </summary>
    public async Task<IReadOnlyList<CapturedEventPhoto>> ReadRecentEventPhotosAsync(Controller controller, int quantity, CancellationToken ct = default)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "hospitalaccess-event-photos", controller.SerialNumber);
        Directory.CreateDirectory(tempDir);

        var cmdDtl = _connections.CreateCommandDetail(controller);
        cmdDtl.Timeout = Math.Max(controller.TimeoutMs, 30000);
        var par = new ReadTransactionAndImageDatabase_Parameter(quantity, true, tempDir);
        var cmd = new ReadTransactionAndImageDatabase(cmdDtl, par);
        await _allocator.AddCommandAsync(cmd);

        var result = cmd.getResult() as ReadTransactionAndImageDatabase_Result
            ?? throw new InvalidOperationException($"ReadTransactionAndImageDatabase não retornou resultado para '{controller.Name}'.");

        var photos = new List<CapturedEventPhoto>();
        try
        {
            var files = Directory.Exists(tempDir)
                ? Directory.GetFiles(tempDir).OrderBy(f => File.GetCreationTimeUtc(f)).ToList()
                : [];
            var records = result.TransactionList.OrderBy(t => t.TransactionDate).ToList();

            for (var i = 0; i < Math.Min(files.Count, records.Count); i++)
            {
                photos.Add(new CapturedEventPhoto(
                    records[i].UserCode,
                    records[i].TransactionDate.ToUniversalTime(),
                    records[i].TransactionCode,
                    await File.ReadAllBytesAsync(files[i], ct)));
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* melhor esforço */ }
        }

        return photos;
    }

    public async Task<IReadOnlyList<uint>> ReadRegisteredUserCodesAsync(Controller controller, CancellationToken ct = default)
    {
        var cmdDtl = _connections.CreateCommandDetail(controller);
        cmdDtl.Timeout = Math.Max(controller.TimeoutMs, 15000); // banco de pessoas pode ser grande
        var cmd = new ReadPersonDataBase(cmdDtl);
        await _allocator.AddCommandAsync(cmd);

        var result = cmd.getResult() as ReadPersonDataBase_Result
            ?? throw new InvalidOperationException($"ReadPersonDataBase não retornou resultado para '{controller.Name}'.");

        return result.PersonList.Select(p => p.UserCode).ToList();
    }

    public async Task<KioskSettingsSnapshot> ReadKioskSettingsAsync(Controller controller, CancellationToken ct = default)
    {
        var languageCmd = new ReadDriveLanguage(_connections.CreateCommandDetail(controller));
        await _allocator.AddCommandAsync(languageCmd);
        var language = languageCmd.getResult() as ReadDriveLanguage_Result;

        var volumeCmd = new ReadDriveVolume(_connections.CreateCommandDetail(controller));
        await _allocator.AddCommandAsync(volumeCmd);
        var volume = volumeCmd.getResult() as ReadDriveVolume_Result;

        var ledCmd = new ReadFaceLEDMode(_connections.CreateCommandDetail(controller));
        await _allocator.AddCommandAsync(ledCmd);
        var led = ledCmd.getResult() as ReadFaceLEDMode_Result;

        var maskCmd = new ReadFaceMouthmufflePar(_connections.CreateCommandDetail(controller));
        await _allocator.AddCommandAsync(maskCmd);
        var mask = maskCmd.getResult() as ReadFaceMouthmufflePar_Result;

        var tempCmd = new ReadFaceBodyTemperaturePar(_connections.CreateCommandDetail(controller));
        await _allocator.AddCommandAsync(tempCmd);
        var temp = tempCmd.getResult() as ReadFaceBodyTemperaturePar_Result;

        var tempAlarmCmd = new ReadFaceBodyTemperatureAlarmPar(_connections.CreateCommandDetail(controller));
        await _allocator.AddCommandAsync(tempAlarmCmd);
        var tempAlarm = tempAlarmCmd.getResult() as ReadFaceBodyTemperatureAlarmPar_Result;

        var tempShowCmd = new ReadFaceBodyTemperatureShowPar(_connections.CreateCommandDetail(controller));
        await _allocator.AddCommandAsync(tempShowCmd);
        var tempShow = tempShowCmd.getResult() as ReadFaceBodyTemperatureShowPar_Result;

        var rangeCmd = new ReadFaceIdentifyRange(_connections.CreateCommandDetail(controller));
        await _allocator.AddCommandAsync(rangeCmd);
        var range = rangeCmd.getResult() as ReadFaceIdentifyRange_Result;

        var livenessCmd = new ReadFaceBioassay(_connections.CreateCommandDetail(controller));
        await _allocator.AddCommandAsync(livenessCmd);
        var liveness = livenessCmd.getResult() as ReadFaceBioassay_Result;

        var livenessSimCmd = new ReadFaceBioassaySimilarity(_connections.CreateCommandDetail(controller));
        await _allocator.AddCommandAsync(livenessSimCmd);
        var livenessSim = livenessSimCmd.getResult() as ReadFaceBioassaySimilarity_Result;

        return new KioskSettingsSnapshot(
            Language: language?.Language ?? 0,
            Volume: volume?.Volume ?? 0,
            FillLightMode: led?.LEDMode ?? 0,
            MaskDetectionMode: mask?.Mouthmuffle ?? 0,
            TemperatureDetectionMode: temp?.BodyTemperaturePar ?? 0,
            TemperatureAlarmThresholdX10: tempAlarm?.AlarmPar ?? 0,
            TemperatureDisplayMode: tempShow?.IsShow ?? 0,
            FaceIdentifyRange: range?.IdentifyRange ?? 0,
            LivenessDetectionMode: liveness?.BioassayType ?? 0,
            LivenessSimilarity: livenessSim?.Similarity ?? 0);
    }

    public async Task WriteKioskSettingsAsync(Controller controller, KioskSettingsSnapshot settings, CancellationToken ct = default)
    {
        await _allocator.AddCommandAsync(new WriteDriveLanguage(
            _connections.CreateCommandDetail(controller), new WriteDriveLanguage_Parameter(settings.Language)));

        await _allocator.AddCommandAsync(new WriteDriveVolume(
            _connections.CreateCommandDetail(controller), new WriteDriveVolume_Parameter(settings.Volume)));

        await _allocator.AddCommandAsync(new WriteFaceLEDMode(
            _connections.CreateCommandDetail(controller), new WriteFaceLEDMode_Parameter(settings.FillLightMode)));

        await _allocator.AddCommandAsync(new WriteFaceMouthmufflePar(
            _connections.CreateCommandDetail(controller), new WriteFaceMouthmufflePar_Parameter(settings.MaskDetectionMode)));

        await _allocator.AddCommandAsync(new WriteFaceBodyTemperaturePar(
            _connections.CreateCommandDetail(controller), new WriteFaceBodyTemperaturePar_Parameter(settings.TemperatureDetectionMode)));

        await _allocator.AddCommandAsync(new WriteFaceBodyTemperatureAlarmPar(
            _connections.CreateCommandDetail(controller), new WriteFaceBodyTemperatureAlarmPar_Parameter(settings.TemperatureAlarmThresholdX10)));

        await _allocator.AddCommandAsync(new WriteFaceBodyTemperatureShowPar(
            _connections.CreateCommandDetail(controller), new WriteFaceBodyTemperatureShowPar_Parameter(settings.TemperatureDisplayMode)));

        await _allocator.AddCommandAsync(new WriteFaceIdentifyRange(
            _connections.CreateCommandDetail(controller), new WriteFaceIdentifyRange_Parameter(settings.FaceIdentifyRange)));

        await _allocator.AddCommandAsync(new WriteFaceBioassay(
            _connections.CreateCommandDetail(controller), new WriteFaceBioassay_Parameter(settings.LivenessDetectionMode)));

        await _allocator.AddCommandAsync(new WriteFaceBioassaySimilarity(
            _connections.CreateCommandDetail(controller), new WriteFaceBioassaySimilarity_Parameter(settings.LivenessSimilarity)));
    }

    /// <summary>
    /// Evento em tempo real empurrado pelo controlador (Classe 9 / authentication record).
    /// Padrão confirmado em ConnectorAllocatorTestClass.cs: o cast para CardTransaction é
    /// feito diretamente sobre o INData original do evento, não sobre uma sub-propriedade.
    /// </summary>
    private void OnTransactionMessage(INConnectorDetail connectorDetail, INData eventData)
    {
        if (eventData is not Door8800Transaction transaction) return;

        // Alarme (Classe IIII): tipo concreto discrimina, independente do CmdIndex do registro de acesso.
        if (eventData is AlarmTransaction alarmTx)
        {
            var rawCode = alarmTx.TransactionCode;
            var cleared = ClearedAlarmCodes.Contains(rawCode);
            var baseCode = rawCode <= 10 ? rawCode : (rawCode & 0x0F);

            AlarmEventReceived?.Invoke(this, new DeviceAlarmEvent
            {
                ControllerSerialNumber = transaction.SN,
                TimestampUtc = alarmTx.TransactionDate.ToUniversalTime(),
                Kind = (AlarmKind)baseCode,
                RawEventCode = rawCode,
                Cleared = cleared,
            });
            return;
        }

        if (transaction.CmdIndex != 1) return; // 1 = registro de autenticação (cartão/face/QR)
        if (eventData is not DoNetDrive.Protocol.Fingerprint.Data.Transaction.CardTransaction card) return;

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
