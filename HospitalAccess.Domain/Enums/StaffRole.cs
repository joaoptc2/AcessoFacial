namespace HospitalAccess.Domain.Enums;

/// <summary>Papéis de quem opera o sistema (não confundir com UserType, que é do lado do acesso físico).</summary>
public enum StaffRole
{
    Admin = 0,
    Operator = 1,
    Reception = 2,
}
