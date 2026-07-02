using HospitalAccess.Domain.Entities;
using HospitalAccess.Infrastructure.Persistence;

namespace HospitalAccess.Api.Services;

/// <summary>
/// Registra uma entrada de histórico administrativo junto do mesmo SaveChangesAsync da
/// operação que a originou (não faz save próprio — só adiciona ao change tracker).
/// </summary>
public static class UserAuditLogger
{
    public static void Record(AccessDbContext db, User user, string action, string? performedBy, string? details = null)
    {
        db.UserAuditLogs.Add(new UserAuditLog
        {
            UserId = user.Id,
            UserCode = user.UserCode,
            UserName = user.Name,
            Action = action,
            PerformedByUsername = performedBy,
            Details = details,
        });
    }
}
