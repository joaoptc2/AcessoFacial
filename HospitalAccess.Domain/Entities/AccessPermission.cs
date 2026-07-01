namespace HospitalAccess.Domain.Entities;

/// <summary>Permissão de um usuário a uma porta, opcionalmente restrita por horário.</summary>
public class AccessPermission
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }
    public User? User { get; set; }

    public Guid DoorId { get; set; }
    public Door? Door { get; set; }

    /// <summary>Grupo de horário do dispositivo (1-64).</summary>
    public int TimeGroup { get; set; } = 1;
}
