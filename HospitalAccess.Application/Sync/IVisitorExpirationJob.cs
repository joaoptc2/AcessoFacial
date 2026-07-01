namespace HospitalAccess.Application.Sync;

/// <summary>
/// Job periódico que expira visitantes vencidos e dispara a revogação nos controladores.
/// A validade também está embutida no QR, mas revogar no device é defesa em profundidade.
/// </summary>
public interface IVisitorExpirationJob
{
    Task RunAsync(CancellationToken ct = default);
}
