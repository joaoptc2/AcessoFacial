using HospitalAccess.Domain.Entities;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace HospitalAccess.Infrastructure.Persistence;

/// <summary>
/// EF Core DbContext (PostgreSQL). AccessLog é append-only por convenção:
/// nunca faça Update/Delete nele. Considere revogar permissões de escrita no banco.
/// </summary>
public class AccessDbContext : DbContext
{
    /// <summary>Nome da sequence do PostgreSQL usada para gerar UserCode de forma atômica e monotônica.</summary>
    public const string UserCodeSequence = "user_code_seq";

    private readonly IDataProtector _secretProtector;

    /// <summary>
    /// O provedor de proteção de dados é injetado pelo container (AddDbContext + AddDataProtection)
    /// e usado para criptografar em repouso a senha de comunicação dos controladores (ver
    /// OnModelCreating). Construtor único: evita ambiguidade de seleção do EF Core.
    /// </summary>
    public AccessDbContext(DbContextOptions<AccessDbContext> options, IDataProtectionProvider dataProtection) : base(options)
    {
        _secretProtector = dataProtection.CreateProtector("HospitalAccess.Controller.CommunicationPassword.v1");
    }

    public DbSet<User> Users => Set<User>();
    public DbSet<UserGroup> UserGroups => Set<UserGroup>();
    public DbSet<Controller> Controllers => Set<Controller>();
    public DbSet<AccessPermission> Permissions => Set<AccessPermission>();
    public DbSet<GroupControllerDefault> GroupControllerDefaults => Set<GroupControllerDefault>();
    public DbSet<UserAuditLog> UserAuditLogs => Set<UserAuditLog>();
    public DbSet<ControllerAuditLog> ControllerAuditLogs => Set<ControllerAuditLog>();
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
        // Sequence dedicada para UserCode: geração atômica entre requisições concorrentes e
        // monotônica (nunca reusa o código de um usuário excluído, o que corromperia o histórico
        // de auditoria/acesso que guarda UserCode como snapshot sem FK).
        b.HasSequence<long>(UserCodeSequence).StartsAt(1).IncrementsBy(1);

        b.Entity<User>().HasIndex(u => u.UserCode).IsUnique();
        b.Entity<User>()
            .HasOne(u => u.Group)
            .WithMany(g => g.Users)
            .HasForeignKey(u => u.GroupId)
            .OnDelete(DeleteBehavior.SetNull);
        // Concorrência otimista: edições simultâneas do mesmo usuário/controlador passam a
        // falhar com DbUpdateConcurrencyException em vez de last-write-wins silencioso.
        b.Entity<User>().UseXminAsConcurrencyToken();

        b.Entity<Controller>().HasIndex(c => c.SerialNumber).IsUnique();
        b.Entity<Controller>().UseXminAsConcurrencyToken();
        b.Entity<Controller>().Property(c => c.ConnectionMode).HasConversion<int>();

        // Senha de comunicação criptografada em repouso (não trafega/armazena em claro). O valor
        // na entidade em memória continua em claro (o gateway usa direto); a conversão só afeta a
        // coluna (o tipo continua text — sem diferença de schema).
        var protector = _secretProtector;
        var encrypted = new ValueConverter<string, string>(
            plain => string.IsNullOrEmpty(plain) ? plain : protector.Protect(plain),
            stored => string.IsNullOrEmpty(stored) ? stored : Unprotect(protector, stored));
        b.Entity<Controller>().Property(c => c.CommunicationPassword).HasConversion(encrypted);

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

        b.Entity<ControllerAuditLog>().HasIndex(a => a.ControllerId);
        b.Entity<ControllerAuditLog>().HasIndex(a => a.TimestampUtc);

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

        // Todas as colunas de data são timestamptz (UTC). O Npgsql rejeita DateTime com Kind
        // Unspecified/Local; este conversor global normaliza a escrita para UTC e marca a leitura
        // como UTC, evitando 500 em inputs sem offset (ex.: <input datetime-local> no Brasil).
        ApplyUtcDateTimeConverter(b);

        base.OnModelCreating(b);
    }

    private static string Unprotect(IDataProtector protector, string stored)
    {
        try { return protector.Unprotect(stored); }
        catch { return stored; } // valor legado gravado em claro antes da criptografia
    }

    private static void ApplyUtcDateTimeConverter(ModelBuilder b)
    {
        var utc = new ValueConverter<DateTime, DateTime>(
            v => v.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(v, DateTimeKind.Utc) : v.ToUniversalTime(),
            v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
        var utcNullable = new ValueConverter<DateTime?, DateTime?>(
            v => v.HasValue ? (v.Value.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v.Value.ToUniversalTime()) : v,
            v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);

        foreach (var entity in b.Model.GetEntityTypes())
        {
            foreach (var prop in entity.GetProperties())
            {
                if (prop.ClrType == typeof(DateTime)) prop.SetValueConverter(utc);
                else if (prop.ClrType == typeof(DateTime?)) prop.SetValueConverter(utcNullable);
            }
        }
    }
}
