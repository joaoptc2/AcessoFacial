namespace HospitalAccess.Gateway;

/// <summary>Configuração de rede local do controlador (Classe I "IP Parameter" / TCPDetail do SDK).</summary>
public sealed record ControllerNetworkInfo(
    string Mac,
    string Ip,
    string IpMask,
    string IpGateway,
    string Dns,
    string DnsBackup,
    int UdpPort,
    string ServerIp,
    string ServerAddr,
    int ServerPort,
    bool AutoIp);

/// <summary>Um controlador encontrado por varredura UDP na rede local (Apêndices 3/5).</summary>
public sealed record DiscoveredController(string SerialNumber, string IpAddress);

/// <summary>Um feriado a ser gravado no controlador (Classe V).</summary>
public sealed record HolidayEntry(byte Index, DateTime Date, bool RepeatsYearly, byte HolidayType);

/// <summary>Uma janela de horário (início-fim) de um dia da semana (Classe VI).</summary>
public sealed record TimeGroupSegmentEntry(byte Weekday, byte SegmentIndex, TimeOnly Begin, TimeOnly End);

/// <summary>Um dos 64 grupos de horário do controlador, com suas janelas por dia da semana.</summary>
public sealed record TimeGroupEntry(int GroupNumber, IReadOnlyList<TimeGroupSegmentEntry> Segments);

/// <summary>
/// Estado consolidado dos alarmes de hardware configuráveis (Classe IIII). O alarme de
/// sensor de porta (magnético) não está incluído: sua configuração de escrita exige uma
/// grade horária completa no protocolo, fora do escopo desta tela — mas o EVENTO de sensor
/// de porta ainda é capturado em tempo real (ver AlarmKind.DoorSensor).
/// </summary>
public sealed record AlarmSettingsSnapshot(
    bool FireAlarmEnabled,
    bool BlacklistAlarmEnabled,
    bool TamperAlarmEnabled,
    bool IllegalVerificationAlarmEnabled,
    byte IllegalVerificationTimes,
    bool DuressAlarmEnabled,
    string? DuressPassword,
    bool OpenDoorTimeoutAlarmEnabled,
    int OpenDoorTimeoutSeconds,
    bool LegalVerificationCloseAlarmEnabled,
    // Modo da senha de coação (AlarmOption do SDK): 1 = trava+alarme, 2 = destrava+alarme,
    // 3 = trava+alarme, só destrava por software. Preservado no roundtrip leitura↔escrita
    // (antes era fixado em 1 na escrita e descartado na leitura). Default 1.
    int DuressMode = 1);

/// <summary>Foto capturada pelo controlador no momento de um evento (Classe XI), obtida por leitura sob demanda.</summary>
public sealed record CapturedEventPhoto(uint? UserCode, DateTime CapturedAtUtc, int RawEventCode, byte[] ImageJpg);

/// <summary>Ajustes locais do quiosque (idioma, volume, luz, máscara, temperatura, distância, detecção de vida).</summary>
public sealed record KioskSettingsSnapshot(
    int Language,
    int Volume,
    int FillLightMode,
    int MaskDetectionMode,
    int TemperatureDetectionMode,
    int TemperatureAlarmThresholdX10,
    int TemperatureDisplayMode,
    int FaceIdentifyRange,
    int LivenessDetectionMode,
    int LivenessSimilarity);
