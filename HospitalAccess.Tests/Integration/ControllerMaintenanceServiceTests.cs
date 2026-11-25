using HospitalAccess.Api.Options;
using HospitalAccess.Api.Services;
using HospitalAccess.Application.Diagnostics;
using HospitalAccess.Domain.Entities;
using HospitalAccess.Domain.Enums;
using HospitalAccess.Infrastructure.Devices;
using HospitalAccess.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace HospitalAccess.Tests.Integration;

/// <summary>
/// Modo manutenção: entrar pausa o tráfego automático da porta; sair devolve à fila o que ficou
/// esperando, na ordem certa (grades primeiro). Banco real, como os demais testes de integração.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ControllerMaintenanceServiceTests
{
    private readonly PostgresFixture _pg;

    public ControllerMaintenanceServiceTests(PostgresFixture pg) => _pg = pg;

    private sealed record Harness(
        ControllerMaintenanceService Maintenance, UserSyncService Sync, ControllerTimeGroupService TimeGroups,
        UserSyncQueue Queue, FakeDeviceGateway Gateway, AccessDbContext Db);

    private Harness Build()
    {
        var db = _pg.CreateContext();
        var gateway = new FakeDeviceGateway();
        var queue = new UserSyncQueue();
        var timeGroups = new ControllerTimeGroupService(db, gateway, NullLogger<ControllerTimeGroupService>.Instance);
        var qr = new DeviceQrService(db,
            new DeviceHttpClientFactory(Options.Create(new DeviceHttpOptions()), NullLogger<DeviceHttpClient>.Instance),
            new NullDeviceTraceSink(), NullLogger<DeviceQrService>.Instance);
        var sync = new UserSyncService(db, gateway, qr, Options.Create(new SyncRetryOptions()),
            TimeZoneInfo.Utc, NullLogger<UserSyncService>.Instance);
        var maintenance = new ControllerMaintenanceService(db, queue, timeGroups, NullLogger<ControllerMaintenanceService>.Instance);
        return new Harness(maintenance, sync, timeGroups, queue, gateway, db);
    }

    private static Controller NewController(string name) => new()
    {
        Name = name,
        IpAddress = "192.168.19.92",
        Port = 8000,
        SerialNumber = Guid.NewGuid().ToString("N")[..16],
        ApiBaseUrl = string.Empty,
    };

    private static User NewUser(Controller controller)
    {
        var user = new User
        {
            UserCode = (uint)Random.Shared.Next(1, int.MaxValue),
            Name = "Fulano de Tal",
            Type = UserType.Permanent,
            FacePhoto = new byte[] { 1, 2, 3 },
        };
        user.Permissions.Add(new AccessPermission { ControllerId = controller.Id });
        return user;
    }

    [SkippableFact]
    public async Task Entrar_MarcaInicioEMotivo_EAudita()
    {
        Skip.IfNot(_pg.Available, _pg.SkipReason);
        var h = Build();
        await using var _ = h.Db;
        var controller = NewController("Quarto 9");
        h.Db.Controllers.Add(controller);
        await h.Db.SaveChangesAsync();

        var state = await h.Maintenance.EnterAsync(controller.Id, "  aguardando troca da fonte  ", "operador1");

        Assert.NotNull(state);
        Assert.True(state.InMaintenance);
        var salvo = await h.Db.Controllers.AsNoTracking().SingleAsync(c => c.Id == controller.Id);
        Assert.NotNull(salvo.MaintenanceSinceUtc);
        Assert.Equal("aguardando troca da fonte", salvo.MaintenanceNote);
        var audit = await h.Db.ControllerAuditLogs.AsNoTracking().SingleAsync(a => a.ControllerId == controller.Id);
        Assert.StartsWith("EntrarEmManutencao", audit.Action);
        Assert.Equal("operador1", audit.PerformedByUsername);
    }

    /// <summary>Reentrar só troca o motivo: a data de início é a da primeira entrada.</summary>
    [SkippableFact]
    public async Task Entrar_DeNovo_MantemOInicio_ETrocaOMotivo()
    {
        Skip.IfNot(_pg.Available, _pg.SkipReason);
        var h = Build();
        await using var _ = h.Db;
        var controller = NewController("Quarto 9");
        controller.MaintenanceSinceUtc = new DateTime(2026, 11, 20, 14, 0, 0, DateTimeKind.Utc);
        h.Db.Controllers.Add(controller);
        await h.Db.SaveChangesAsync();

        var state = await h.Maintenance.EnterAsync(controller.Id, "fonte trocada, aguardando cabo", "operador1");

        Assert.Equal(controller.MaintenanceSinceUtc, state!.SinceUtc);
        var salvo = await h.Db.Controllers.AsNoTracking().SingleAsync(c => c.Id == controller.Id);
        Assert.Equal(controller.MaintenanceSinceUtc, salvo.MaintenanceSinceUtc);
        Assert.Equal("fonte trocada, aguardando cabo", salvo.MaintenanceNote);
        Assert.False(await h.Db.ControllerAuditLogs.AnyAsync(a => a.ControllerId == controller.Id));
    }

    /// <summary>
    /// O ciclo inteiro: cadastro feito durante a manutenção espera sem tocar o aparelho; na saída,
    /// as grades vão primeiro, o pendente vence na hora e entra na fila, e o cadastro chega.
    /// Portas e usuários de fora não são mexidos.
    /// </summary>
    [SkippableFact]
    public async Task Sair_EnviaAsGrades_EDevolveAFilaOQueFicouEsperando()
    {
        Skip.IfNot(_pg.Available, _pg.SkipReason);
        var h = Build();
        await using var _ = h.Db;
        var porta = NewController("Quarto 9");
        var outra = NewController("Quarto 1");
        var esperando = NewUser(porta);
        var jaCadastrado = NewUser(porta);
        var deOutraPorta = NewUser(outra);
        h.Db.Controllers.AddRange(porta, outra);
        h.Db.Users.AddRange(esperando, jaCadastrado, deOutraPorta);
        await h.Db.SaveChangesAsync();

        await h.Sync.SyncUserAsync(jaCadastrado.Id); // antes da manutenção
        await h.Maintenance.EnterAsync(porta.Id, null, "operador1");
        h.Db.ChangeTracker.Clear();
        await h.Sync.SyncUserAsync(esperando.Id);   // durante: fica esperando
        var falhaNaOutra = new DeviceSyncStatus
        {
            UserId = deOutraPorta.Id, ControllerId = outra.Id, State = SyncState.Failed,
            NextRetryAtUtc = DateTime.UtcNow.AddHours(1), LastError = "timeout",
        };
        h.Db.SyncStatuses.Add(falhaNaOutra);
        await h.Db.SaveChangesAsync();
        Assert.Single(h.Gateway.FaceUploads); // só o de antes da manutenção

        var state = await h.Maintenance.LeaveAsync(porta.Id, "operador1");

        Assert.False(state!.InMaintenance);
        Assert.True(state.TimeGroupsPushed);
        Assert.Equal(new[] { porta.Id }, h.Gateway.TimeGroupPushes);
        Assert.Equal(1, state.Requeued);
        Assert.Contains(esperando.Id, h.Queue.ActiveUserIds);
        Assert.DoesNotContain(jaCadastrado.Id, h.Queue.ActiveUserIds);
        Assert.DoesNotContain(deOutraPorta.Id, h.Queue.ActiveUserIds);

        var status = await h.Db.SyncStatuses.AsNoTracking().SingleAsync(s => s.UserId == esperando.Id);
        Assert.True(status.NextRetryAtUtc <= DateTime.UtcNow, "o pendente deveria vencer na saída da manutenção");
        var outraIntacta = await h.Db.SyncStatuses.AsNoTracking().SingleAsync(s => s.UserId == deOutraPorta.Id);
        Assert.True(outraIntacta.NextRetryAtUtc > DateTime.UtcNow.AddMinutes(50), "status de outra porta foi mexido");
        var salvo = await h.Db.Controllers.AsNoTracking().SingleAsync(c => c.Id == porta.Id);
        Assert.Null(salvo.MaintenanceSinceUtc);
        Assert.Null(salvo.MaintenanceNote);

        // E o cadastro chega ao aparelho quando o worker processar a fila.
        h.Db.ChangeTracker.Clear();
        await h.Sync.SyncUserAsync(esperando.Id);
        Assert.Equal(2, h.Gateway.FaceUploads.Count);
        Assert.Equal(SyncState.Synced,
            (await h.Db.SyncStatuses.AsNoTracking().SingleAsync(s => s.UserId == esperando.Id)).State);
    }

    /// <summary>Sair com a porta ainda muda: a saída vale, mas a resposta avisa que as grades não chegaram.</summary>
    [SkippableFact]
    public async Task Sair_ComAPortaAindaMuda_AvisaQueAsGradesNaoChegaram()
    {
        Skip.IfNot(_pg.Available, _pg.SkipReason);
        var h = Build();
        await using var _ = h.Db;
        var porta = NewController("Quarto 9");
        porta.MaintenanceSinceUtc = DateTime.UtcNow.AddDays(-3);
        h.Db.Controllers.Add(porta);
        await h.Db.SaveChangesAsync();
        h.Gateway.ThrowingControllers[porta.Id] = "sem resposta";

        var state = await h.Maintenance.LeaveAsync(porta.Id, "operador1");

        Assert.False(state!.InMaintenance);
        Assert.False(state.TimeGroupsPushed);
    }

    /// <summary>Mudar uma grade com a porta em manutenção não faz o operador esperar o teto de 20 s.</summary>
    [SkippableFact]
    public async Task GradeAlteradaEmManutencao_NaoTentaOAparelho()
    {
        Skip.IfNot(_pg.Available, _pg.SkipReason);
        var h = Build();
        await using var _ = h.Db;
        var porta = NewController("Quarto 9");
        porta.MaintenanceSinceUtc = DateTime.UtcNow;
        h.Db.Controllers.Add(porta);
        await h.Db.SaveChangesAsync();

        Assert.False(await h.TimeGroups.TryPushAsync(porta.Id));
        Assert.Empty(h.Gateway.TimeGroupPushes);
    }

    [SkippableFact]
    public async Task ControladorInexistente_DevolveNull()
    {
        Skip.IfNot(_pg.Available, _pg.SkipReason);
        var h = Build();
        await using var _ = h.Db;

        Assert.Null(await h.Maintenance.EnterAsync(Guid.NewGuid(), null, "x"));
        Assert.Null(await h.Maintenance.LeaveAsync(Guid.NewGuid(), "x"));
    }
}
