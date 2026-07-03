using HospitalAccess.Application.Sync;
using HospitalAccess.Domain.Enums;
using HospitalAccess.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace HospitalAccess.Api.Services;

/// <summary>
/// Expira visitantes vencidos. A validade já está embutida no QR (Appendix 8) e o
/// controlador a valida offline — isso já bloqueia o acesso sozinho. Este job existe
/// como defesa em profundidade e para higiene dos dados: marca o visitante como
/// expirado e dispara RevokeUserAsync (que, para visitantes sem Person cadastrada no
/// device, é um no-op de hardware, mas ainda audita a revogação no banco).
/// </summary>
public sealed class VisitorExpirationJob : IVisitorExpirationJob
{
    private readonly AccessDbContext _db;
    private readonly IUserSyncService _sync;
    private readonly ILogger<VisitorExpirationJob> _logger;

    public VisitorExpirationJob(AccessDbContext db, IUserSyncService sync, ILogger<VisitorExpirationJob> logger)
    {
        _db = db;
        _sync = sync;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        // Só visitantes vencidos que ainda NÃO foram revogados — senão o job reprocessaria os
        // mesmos vencidos a cada execução, para sempre.
        var expired = await _db.Users
            .Where(u => u.Type == UserType.Visitor
                        && u.ValidUntil != null && u.ValidUntil < now
                        && u.RevokedAtUtc == null)
            .ToListAsync(ct);

        foreach (var visitor in expired)
        {
            try
            {
                await _sync.RevokeUserAsync(visitor.Id, ct);

                // Marca a revogação por expiração no próprio cadastro (higiene de dados e para
                // não reprocessar) e audita.
                visitor.RevokedAtUtc = now;
                visitor.RevokedByUsername = "sistema (expiração automática)";
                UserAuditLogger.Record(_db, visitor, "Expirado", "sistema");
                await _db.SaveChangesAsync(ct);

                _logger.LogInformation("Visitante {UserId} ({UserCode}) expirado e revogado.", visitor.Id, visitor.UserCode);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha ao revogar visitante expirado {UserId}.", visitor.Id);
            }
        }
    }
}
