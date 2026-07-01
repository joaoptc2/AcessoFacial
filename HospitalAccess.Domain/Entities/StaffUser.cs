using HospitalAccess.Domain.Enums;

namespace HospitalAccess.Domain.Entities;

/// <summary>Operador humano do sistema (admin/operador/recepção) — distinto de User (pessoa com acesso físico).</summary>
public class StaffUser
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Username { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public StaffRole Role { get; set; }
    public bool Active { get; set; } = true;
}
