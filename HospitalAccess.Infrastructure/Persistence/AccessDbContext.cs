using HospitalAccess.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace HospitalAccess.Infrastructure.Persistence;

/// <summary>
/// EF Core DbContext (PostgreSQL). AccessLog é append-only por convenção:
/// nunca faça Update/Delete nele. Considere revogar permissões de escrita no banco.
/// </summary>
public class AccessDbContext : DbContext
{
    public AccessDbContext(DbContextOptions<AccessDbContext> options) : base(options) { }

    public DbSet<User> Users => Set<User>();
    public DbSet<Controller> Controllers => Set<Controller>();
    public DbSet<Door> Doors => Set<Door>();
    public DbSet<AccessPermission> Permissions => Set<AccessPermission>();
    public DbSet<DeviceSyncStatus> SyncStatuses => Set<DeviceSyncStatus>();
    public DbSet<AccessLog> AccessLogs => Set<AccessLog>();
    public DbSet<StaffUser> StaffUsers => Set<StaffUser>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<User>().HasIndex(u => u.UserCode).IsUnique();
        b.Entity<Controller>().HasIndex(c => c.SerialNumber).IsUnique();

        b.Entity<DeviceSyncStatus>()
            .HasIndex(s => new { s.UserId, s.ControllerId }).IsUnique();

        b.Entity<AccessLog>().HasIndex(l => l.TimestampUtc);

        b.Entity<StaffUser>().HasIndex(s => s.Username).IsUnique();

        // TODO: senhas de comunicação NÃO devem ser persistidas em claro.
        //       Carregar de secrets/config, ou criptografar em repouso.
        base.OnModelCreating(b);
    }
}
