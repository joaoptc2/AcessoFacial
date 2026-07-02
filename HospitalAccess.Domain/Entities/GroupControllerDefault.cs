namespace HospitalAccess.Domain.Entities;

/// <summary>
/// Porta que faz parte do padrão de um grupo organizacional: ao associar um usuário a este
/// grupo, o front-end pré-seleciona estas portas nas permissões dele (AccessPermission).
/// Não é uma restrição imposta pelo servidor — o usuário pode ter portas adicionais ou ter
/// alguma destas removida individualmente depois da associação.
/// </summary>
public class GroupControllerDefault
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid GroupId { get; set; }
    public UserGroup? Group { get; set; }

    public Guid ControllerId { get; set; }
    public Controller? Controller { get; set; }
}
