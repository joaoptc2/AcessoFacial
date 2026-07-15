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

    /// <summary>
    /// Origem da permissão: quando preenchido, esta porta foi HERDADA do grupo organizacional
    /// (GroupControllerDefault) e é gerida pelo grupo — some se a porta sair do grupo ou o usuário
    /// mudar/sair do grupo. <c>null</c> = adicionada MANUALMENTE ao usuário (sobrevive a mudanças de
    /// grupo). É apenas uma tag informativa (sem FK): a limpeza ao excluir um grupo é feita em código
    /// (GroupAccessService), não por cascade no banco.
    /// </summary>
    public Guid? GrantedByGroupId { get; set; }
}
