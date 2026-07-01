namespace HospitalAccess.Domain.Entities;

/// <summary>Porta associada a um controlador (relé).</summary>
public class Door
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;

    public Guid ControllerId { get; set; }
    public Controller? Controller { get; set; }

    /// <summary>Índice do relé/porta no controlador.</summary>
    public int RelayIndex { get; set; }
}
