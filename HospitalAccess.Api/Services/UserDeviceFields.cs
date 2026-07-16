using HospitalAccess.Domain.Entities;

namespace HospitalAccess.Api.Services;

/// <summary>
/// Decide se uma edição de usuário precisa ser re-enviada aos controladores. Só interessam os
/// campos que o gateway realmente grava no aparelho (ver AddPersonWithFaceAsync): nome, grupo
/// de horário, número do cartão e a foto de face. Editar telefone/e-mail/notas/cargo etc. não
/// muda nada no dispositivo — re-enviar a face (~120 KB por porta) nesses casos era uma fonte
/// gratuita de tráfego.
/// </summary>
public static class UserDeviceFields
{
    /// <summary>Compare ANTES de aplicar a edição na entidade (senão nunca há diferença).</summary>
    public static bool Changed(User current, string newName, int newTimeGroup, uint? newCardNumber, bool hasNewPhoto) =>
        hasNewPhoto
        || current.Name != newName
        || current.TimeGroup != newTimeGroup
        || current.CardNumber != newCardNumber;
}
