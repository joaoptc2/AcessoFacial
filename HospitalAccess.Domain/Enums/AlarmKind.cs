namespace HospitalAccess.Domain.Enums;

/// <summary>
/// Tipo de alarme reportado pelo controlador (AlarmTransaction.TransactionCode do SDK,
/// código base sem o bit de "cancelamento" — ver AlarmEvent.Cleared).
/// </summary>
public enum AlarmKind
{
    DoorSensor = 1,       // porta arrombada/forçada (magnético de porta)
    Panic = 2,            // pânico/assalto
    Fire = 3,             // incêndio
    InvalidCard = 4,      // tentativa de credencial inválida (verificação ilegal)
    Duress = 5,           // coação (senha de pânico)
    FireCommand = 6,      // incêndio (disparado por comando)
    Smoke = 7,            // fumaça
    AntiTheft = 8,        // antifurto / sabotagem (tamper)
    Blacklist = 9,        // correspondência com lista negra
    OpenDoorTimeout = 10, // porta aberta além do tempo configurado
}
