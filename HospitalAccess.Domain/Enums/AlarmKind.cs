namespace HospitalAccess.Domain.Enums;

/// <summary>
/// Tipo de alarme normalizado. Os alarmes chegam como LOG DE SISTEMA do controlador
/// (SystemTransaction, push com CmdIndex == 3 — protocolo Classe IX §9.4) e são mapeados
/// por TransactionCodeClassifier.TryMapSystemAlarm: códigos 14–20 disparam e 21–27 limpam
/// (ver AlarmEvent.Cleared). Os valores abaixo são a numeração interna do sistema, não os
/// códigos crus do protocolo.
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
