namespace HospitalAccess.Domain.Enums;

public enum UserType
{
    Permanent = 0, // funcionário/usuário fixo (acesso por face)
    Visitor = 1    // temporário (acesso por QR com validade embutida)
}
