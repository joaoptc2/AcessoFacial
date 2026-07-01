namespace HospitalAccess.Domain.Enums;

/// <summary>Como o acesso foi realizado / concedido.</summary>
public enum AccessMethod
{
    Face = 1,
    QrCode = 2,
    RemoteOpen = 3, // porta aberta remotamente pelo operador
    Card = 4
}
