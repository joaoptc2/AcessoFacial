namespace HospitalAccess.Domain.Entities;

/// <summary>Permissão de um usuário a um controlador (porta), opcionalmente restrita por horário.</summary>
public class AccessPermission
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }
    public User? User { get; set; }

    public Guid ControllerId { get; set; }
    public Controller? Controller { get; set; }

    /// <summary>Grupo de horário do dispositivo (1-64).</summary>
    public int TimeGroup { get; set; } = 1;
}
