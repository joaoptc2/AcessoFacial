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
    public DbSet<UserGroup> UserGroups => Set<UserGroup>();
    public DbSet<Controller> Controllers => Set<Controller>();
    public DbSet<AccessPermission> Permissions => Set<AccessPermission>();
    public DbSet<GroupControllerDefault> GroupControllerDefaults => Set<GroupControllerDefault>();
    public DbSet<UserAuditLog> UserAuditLogs => Set<UserAuditLog>();
    public DbSet<DeviceSyncStatus> SyncStatuses => Set<DeviceSyncStatus>();
    public DbSet<AccessLog> AccessLogs => Set<AccessLog>();
    public DbSet<StaffUser> StaffUsers => Set<StaffUser>();
    public DbSet<AlarmEvent> AlarmEvents => Set<AlarmEvent>();
    public DbSet<EventPhoto> EventPhotos => Set<EventPhoto>();
    public DbSet<Holiday> Holidays => Set<Holiday>();
    public DbSet<TimeGroupSchedule> TimeGroupSchedules => Set<TimeGroupSchedule>();
    public DbSet<TimeGroupSegment> TimeGroupSegments => Set<TimeGroupSegment>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<User>().HasIndex(u => u.UserCode).IsUnique();
        b.Entity<User>()
            .HasOne(u => u.Group)
            .WithMany(g => g.Users)
            .HasForeignKey(u => u.GroupId)
            .OnDelete(DeleteBehavior.SetNull);

        b.Entity<Controller>().HasIndex(c => c.SerialNumber).IsUnique();

        b.Entity<GroupControllerDefault>().HasIndex(d => new { d.GroupId, d.ControllerId }).IsUnique();
        b.Entity<GroupControllerDefault>()
            .HasOne(d => d.Group)
            .WithMany(g => g.DefaultControllers)
            .HasForeignKey(d => d.GroupId)
            .OnDelete(DeleteBehavior.Cascade);
        b.Entity<GroupControllerDefault>()
            .HasOne(d => d.Controller)
            .WithMany()
            .HasForeignKey(d => d.ControllerId)
            .OnDelete(DeleteBehavior.Cascade);

        b.Entity<DeviceSyncStatus>()
            .HasIndex(s => new { s.UserId, s.ControllerId }).IsUnique();

        b.Entity<AccessLog>().HasIndex(l => l.TimestampUtc);

        b.Entity<UserAuditLog>().HasIndex(a => a.UserId);
        b.Entity<UserAuditLog>().HasIndex(a => a.TimestampUtc);

        b.Entity<StaffUser>().HasIndex(s => s.Username).IsUnique();

        b.Entity<AlarmEvent>().HasIndex(a => a.TimestampUtc);

        b.Entity<Holiday>().HasIndex(h => h.Index).IsUnique();

        b.Entity<TimeGroupSchedule>().HasIndex(t => t.GroupNumber).IsUnique();
        b.Entity<TimeGroupSegment>()
            .HasOne(s => s.Schedule)
            .WithMany(t => t.Segments)
            .HasForeignKey(s => s.TimeGroupScheduleId)
            .OnDelete(DeleteBehavior.Cascade);
        b.Entity<TimeGroupSegment>()
            .HasIndex(s => new { s.TimeGroupScheduleId, s.Weekday, s.SegmentIndex }).IsUnique();

        // TODO: senhas de comunicação NÃO devem ser persistidas em claro.
        //       Carregar de secrets/config, ou criptografar em repouso.
        base.OnModelCreating(b);
    }
}
