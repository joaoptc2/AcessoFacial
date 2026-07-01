namespace HospitalAccess.Domain.Entities;

/// <summary>Agrupamento de usuários para organização (ex.: "Enfermagem", "Manutenção", "Administrativo").</summary>
public class UserGroup
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    public ICollection<User> Users { get; set; } = new List<User>();
}
