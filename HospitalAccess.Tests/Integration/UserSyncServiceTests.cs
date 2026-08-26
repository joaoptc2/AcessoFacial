using HospitalAccess.Api.Options;
using HospitalAccess.Api.Services;
using HospitalAccess.Domain.Entities;
using HospitalAccess.Domain.Enums;
using HospitalAccess.Gateway;
using HospitalAccess.Infrastructure.Devices;
using HospitalAccess.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace HospitalAccess.Tests.Integration;

/// <summary>
/// A máquina de estado do <c>UserSyncService</c> — quem decide se uma credencial fica ATIVA ou
/// não em cada porta. Era a classe mais crítica do sistema sem nenhum teste, e é onde os bugs de
/// produção apareceram (os comentários do próprio arquivo contam quais).
///
/// <para>
/// Cada teste monta o estado no banco, roda o serviço contra um gateway falso e verifica DUAS
/// coisas: o que foi pedido ao hardware (a credencial saiu/entrou de fato) e como o
/// <c>DeviceSyncStatus</c> ficou (se vai ser re-tentado, quando, ou se caiu em quarentena).
/// </para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class UserSyncServiceTests
{
    private readonly PostgresFixture _pg;

    public UserSyncServiceTests(PostgresFixture pg) => _pg = pg;

    // ---------------------------------------------------------------- helpers

    private static readonly SyncRetryOptions RetryOptions = new()
    {
        BackoffBaseMinutes = 2,
        BackoffFactor = 2,
        BackoffCapMinutes = 60,
    };

    private (UserSyncService Service, FakeDeviceGateway Gateway, AccessDbContext Db) BuildService()
    {
        var db = _pg.CreateContext();
        var gateway = new FakeDeviceGateway();

        // DeviceQrService real: com ApiBaseUrl vazio nos controladores de teste, CanUseHttp é
        // false e ele não toca a rede — é justamente o caminho "controlador só-SDK".
        var httpFactory = new DeviceHttpClientFactory(
            Options.Create(new DeviceHttpOptions()),
            NullLogger<DeviceHttpClient>.Instance);
        var qr = new DeviceQrService(db, httpFactory, NullLogger<DeviceQrService>.Instance);

        var service = new UserSyncService(
            db, gateway, qr,
            Options.Create(RetryOptions),
            TimeZoneInfo.Utc,
            NullLogger<UserSyncService>.Instance);

        return (service, gateway, db);
    }

    /// <summary>Controlador de teste sem painel HTTP (só SDK) — evita qualquer caminho de rede.</summary>
    private static Controller NewController(string name) => new()
    {
        Name = name,
        IpAddress = "192.168.19.20",
        Port = 8000,
        SerialNumber = Guid.NewGuid().ToString("N")[..16],
        ApiBaseUrl = string.Empty,
    };

    private static User NewPermanentUser(uint code, string name = "Fulano de Tal") => new()
    {
        UserCode = code,
        Name = name,
        Type = UserType.Permanent,
        FacePhoto = new byte[] { 1, 2, 3 },
    };

    private static uint NextCode() => (uint)Random.Shared.Next(1, int.MaxValue);

    // ---------------------------------------------------------------- testes

    [SkippableFact]
    public async Task Sincroniza_UsuarioPermanenteComPermissao_EMarcaSynced()
    {
        Skip.IfNot(_pg.Available, _pg.SkipReason);
        var (service, gateway, db) = BuildService();
        await using var _ = db;

        var controller = NewController("Portaria");
        var user = NewPermanentUser(NextCode());
        user.Permissions.Add(new AccessPermission { ControllerId = controller.Id });
        db.Controllers.Add(controller);
        db.Users.Add(user);
        await db.SaveChangesAsync();

        await service.SyncUserAsync(user.Id);

        Assert.Single(gateway.FaceUploads);
        var status = await db.SyncStatuses.AsNoTracking().SingleAsync(s => s.UserId == user.Id);
        Assert.Equal(SyncState.Synced, status.State);
        Assert.Null(status.LastError);
        Assert.Null(status.NextRetryAtUtc);
    }

    [SkippableFact]
    public async Task AparelhoInacessivel_MarcaFailed_ComBackoffAgendado()
    {
        Skip.IfNot(_pg.Available, _pg.SkipReason);
        var (service, gateway, db) = BuildService();
        await using var _ = db;

        var controller = NewController("Ala Norte");
        var user = NewPermanentUser(NextCode());
        user.Permissions.Add(new AccessPermission { ControllerId = controller.Id });
        db.Controllers.Add(controller);
        db.Users.Add(user);
        await db.SaveChangesAsync();

        gateway.ThrowingControllers[controller.Id] = "aparelho offline";

        await service.SyncUserAsync(user.Id);

        var status = await db.SyncStatuses.AsNoTracking().SingleAsync(s => s.UserId == user.Id);
        Assert.Equal(SyncState.Failed, status.State);
        Assert.Equal(1, status.RetryCount);
        // Transitória: PRECISA ter data de nova tentativa, senão o usuário fica preso em quarentena.
        Assert.NotNull(status.NextRetryAtUtc);
        Assert.Contains("offline", status.LastError);
    }

    [SkippableFact]
    public async Task FotoSemRosto_VaiParaQuarentena_SemNovaTentativaAutomatica()
    {
        Skip.IfNot(_pg.Available, _pg.SkipReason);
        var (service, gateway, db) = BuildService();
        await using var _ = db;

        var controller = NewController("Farmácia");
        var user = NewPermanentUser(NextCode());
        user.Permissions.Add(new AccessPermission { ControllerId = controller.Id });
        db.Controllers.Add(controller);
        db.Users.Add(user);
        await db.SaveChangesAsync();

        gateway.FaceResultByController[controller.Id] =
            new AddFaceResult(false, FaceUploadCode.NoFaceInPhoto, "Nenhum rosto reconhecível na foto.");

        await service.SyncUserAsync(user.Id);

        var status = await db.SyncStatuses.AsNoTracking().SingleAsync(s => s.UserId == user.Id);
        Assert.Equal(SyncState.Failed, status.State);
        // Quarentena = NextRetryAtUtc null. Sem isso, a foto de ~120 KB seria reenviada a cada
        // varredura de 2 min, para sempre — a principal fonte de sobrecarga da rede dos aparelhos.
        Assert.Null(status.NextRetryAtUtc);
    }

    [SkippableFact]
    public async Task FotoQueNaoConverte_TambemVaiParaQuarentena()
    {
        Skip.IfNot(_pg.Available, _pg.SkipReason);
        var (service, gateway, db) = BuildService();
        await using var _ = db;

        var controller = NewController("Almoxarifado");
        var user = NewPermanentUser(NextCode());
        user.Permissions.Add(new AccessPermission { ControllerId = controller.Id });
        db.Controllers.Add(controller);
        db.Users.Add(user);
        await db.SaveChangesAsync();

        gateway.FaceResultByController[controller.Id] =
            new AddFaceResult(false, FaceUploadCode.InvalidImage, "arquivo corrompido");

        await service.SyncUserAsync(user.Id);

        var status = await db.SyncStatuses.AsNoTracking().SingleAsync(s => s.UserId == user.Id);
        Assert.Equal(SyncState.Failed, status.State);
        Assert.Null(status.NextRetryAtUtc);
    }

    [SkippableFact]
    public async Task FaceDuplicada_GuardaOCodigoEmConflito_ParaAUiOferecerResolucao()
    {
        Skip.IfNot(_pg.Available, _pg.SkipReason);
        var (service, gateway, db) = BuildService();
        await using var _ = db;

        var controller = NewController("Recepção");
        var user = NewPermanentUser(NextCode());
        user.Permissions.Add(new AccessPermission { ControllerId = controller.Id });
        db.Controllers.Add(controller);
        db.Users.Add(user);
        await db.SaveChangesAsync();

        gateway.FaceResultByController[controller.Id] =
            new AddFaceResult(false, FaceUploadCode.Duplicate, "duplicada", ConflictUserCode: 4242);

        await service.SyncUserAsync(user.Id);

        var status = await db.SyncStatuses.AsNoTracking().SingleAsync(s => s.UserId == user.Id);
        Assert.Equal(SyncState.Failed, status.State);
        Assert.Equal(4242u, status.ConflictUserCode);
        Assert.Null(status.NextRetryAtUtc);
    }

    [SkippableFact]
    public async Task JaSynced_NaoReenviaAFace()
    {
        Skip.IfNot(_pg.Available, _pg.SkipReason);
        var (service, gateway, db) = BuildService();
        await using var _ = db;

        var controller = NewController("Portaria 2");
        var user = NewPermanentUser(NextCode());
        user.Permissions.Add(new AccessPermission { ControllerId = controller.Id });
        db.Controllers.Add(controller);
        db.Users.Add(user);
        db.SyncStatuses.Add(new DeviceSyncStatus
        {
            UserId = user.Id,
            ControllerId = controller.Id,
            State = SyncState.Synced,
        });
        await db.SaveChangesAsync();

        await service.SyncUserAsync(user.Id);

        // Idempotência: reenfileirar um usuário já sincronizado não pode custar tráfego de face.
        Assert.Empty(gateway.FaceUploads);
    }

    [SkippableFact]
    public async Task BackoffAindaNaoVenceu_NaoTentaDeNovo()
    {
        Skip.IfNot(_pg.Available, _pg.SkipReason);
        var (service, gateway, db) = BuildService();
        await using var _ = db;

        var controller = NewController("Ala Sul");
        var user = NewPermanentUser(NextCode());
        user.Permissions.Add(new AccessPermission { ControllerId = controller.Id });
        db.Controllers.Add(controller);
        db.Users.Add(user);
        db.SyncStatuses.Add(new DeviceSyncStatus
        {
            UserId = user.Id,
            ControllerId = controller.Id,
            State = SyncState.Failed,
            RetryCount = 1,
            NextRetryAtUtc = DateTime.UtcNow.AddMinutes(30), // janela ainda aberta
        });
        await db.SaveChangesAsync();

        await service.SyncUserAsync(user.Id);

        Assert.Empty(gateway.FaceUploads);
    }

    [SkippableFact]
    public async Task BackoffVencido_TentaDeNovo()
    {
        Skip.IfNot(_pg.Available, _pg.SkipReason);
        var (service, gateway, db) = BuildService();
        await using var _ = db;

        var controller = NewController("Ala Leste");
        var user = NewPermanentUser(NextCode());
        user.Permissions.Add(new AccessPermission { ControllerId = controller.Id });
        db.Controllers.Add(controller);
        db.Users.Add(user);
        db.SyncStatuses.Add(new DeviceSyncStatus
        {
            UserId = user.Id,
            ControllerId = controller.Id,
            State = SyncState.Failed,
            RetryCount = 1,
            NextRetryAtUtc = DateTime.UtcNow.AddMinutes(-1), // venceu
        });
        await db.SaveChangesAsync();

        await service.SyncUserAsync(user.Id);

        Assert.Single(gateway.FaceUploads);
        var status = await db.SyncStatuses.AsNoTracking().SingleAsync(s => s.UserId == user.Id);
        Assert.Equal(SyncState.Synced, status.State);
    }

    [SkippableFact]
    public async Task Quarentena_NaoEhDestravadaPelaVarredura()
    {
        Skip.IfNot(_pg.Available, _pg.SkipReason);
        var (service, gateway, db) = BuildService();
        await using var _ = db;

        var controller = NewController("Depósito");
        var user = NewPermanentUser(NextCode());
        user.Permissions.Add(new AccessPermission { ControllerId = controller.Id });
        db.Controllers.Add(controller);
        db.Users.Add(user);
        db.SyncStatuses.Add(new DeviceSyncStatus
        {
            UserId = user.Id,
            ControllerId = controller.Id,
            State = SyncState.Failed,
            RetryCount = 3,
            NextRetryAtUtc = null, // quarentena: só ação manual tira daqui
        });
        await db.SaveChangesAsync();

        await service.SyncUserAsync(user.Id);

        Assert.Empty(gateway.FaceUploads);
    }

    /// <summary>
    /// O caso mais importante de todos: uma porta retirada da pessoa precisa virar REMOÇÃO no
    /// aparelho. O status daquela porta é mantido 'Synced' de propósito pelo controller — é a
    /// partir dele que a revogação parte. Se isso quebrar, a credencial fica ativa no hardware
    /// de uma porta que a pessoa não deveria mais abrir.
    /// </summary>
    [SkippableFact]
    public async Task PermissaoRemovida_RevogaNoAparelho()
    {
        Skip.IfNot(_pg.Available, _pg.SkipReason);
        var (service, gateway, db) = BuildService();
        await using var _ = db;

        var mantida = NewController("Porta mantida");
        var removida = NewController("Porta removida");
        var user = NewPermanentUser(NextCode());
        user.Permissions.Add(new AccessPermission { ControllerId = mantida.Id }); // só a mantida
        db.Controllers.AddRange(mantida, removida);
        db.Users.Add(user);
        db.SyncStatuses.AddRange(
            new DeviceSyncStatus { UserId = user.Id, ControllerId = mantida.Id, State = SyncState.Synced },
            new DeviceSyncStatus { UserId = user.Id, ControllerId = removida.Id, State = SyncState.Synced });
        await db.SaveChangesAsync();

        await service.SyncUserAsync(user.Id);

        var deletion = Assert.Single(gateway.Deletions);
        Assert.Equal(removida.Id, deletion.ControllerId);
        Assert.Equal(user.UserCode, deletion.UserCode);

        var status = await db.SyncStatuses.AsNoTracking()
            .SingleAsync(s => s.UserId == user.Id && s.ControllerId == removida.Id);
        Assert.Equal(SyncState.Revoked, status.State);
    }

    /// <summary>
    /// Regressão registrada nos comentários do serviço: um usuário revogado com uma revogação que
    /// FALHOU (aparelho offline na hora) precisa ser re-tentado. Antes, o método retornava cedo e
    /// a credencial ficava ATIVA no hardware para sempre.
    /// </summary>
    [SkippableFact]
    public async Task UsuarioRevogado_RetentaARevogacaoQueFalhou()
    {
        Skip.IfNot(_pg.Available, _pg.SkipReason);
        var (service, gateway, db) = BuildService();
        await using var _ = db;

        var controller = NewController("Ala Oeste");
        var user = NewPermanentUser(NextCode());
        user.RevokedAtUtc = DateTime.UtcNow.AddMinutes(-10);
        user.Permissions.Add(new AccessPermission { ControllerId = controller.Id });
        db.Controllers.Add(controller);
        db.Users.Add(user);
        db.SyncStatuses.Add(new DeviceSyncStatus
        {
            UserId = user.Id,
            ControllerId = controller.Id,
            State = SyncState.Synced,
        });
        await db.SaveChangesAsync();

        await service.SyncUserAsync(user.Id);

        // Nunca (re)cadastrar um revogado, mesmo com permissão presente...
        Assert.Empty(gateway.FaceUploads);
        // ...e a revogação tem de chegar ao aparelho.
        Assert.Single(gateway.Deletions);
        var status = await db.SyncStatuses.AsNoTracking().SingleAsync(s => s.UserId == user.Id);
        Assert.Equal(SyncState.Revoked, status.State);
    }

    /// <summary>
    /// Isolamento por porta: uma porta que falha não pode abortar as demais. O sintoma em produção
    /// era "o usuário nem aparece no status de sincronização" das outras controladoras.
    /// </summary>
    [SkippableFact]
    public async Task FalhaEmUmaPorta_NaoImpedeAsOutras()
    {
        Skip.IfNot(_pg.Available, _pg.SkipReason);
        var (service, gateway, db) = BuildService();
        await using var _ = db;

        var ruim = NewController("Porta com defeito");
        var boa1 = NewController("Porta boa 1");
        var boa2 = NewController("Porta boa 2");
        var user = NewPermanentUser(NextCode());
        foreach (var c in new[] { ruim, boa1, boa2 })
            user.Permissions.Add(new AccessPermission { ControllerId = c.Id });
        db.Controllers.AddRange(ruim, boa1, boa2);
        db.Users.Add(user);
        await db.SaveChangesAsync();

        gateway.ThrowingControllers[ruim.Id] = "timeout";

        await service.SyncUserAsync(user.Id);

        Assert.Equal(2, gateway.FaceUploads.Count);
        var statuses = await db.SyncStatuses.AsNoTracking().Where(s => s.UserId == user.Id).ToListAsync();
        Assert.Equal(3, statuses.Count); // TODAS as portas têm linha de status
        Assert.Equal(SyncState.Failed, statuses.Single(s => s.ControllerId == ruim.Id).State);
        Assert.All(statuses.Where(s => s.ControllerId != ruim.Id), s => Assert.Equal(SyncState.Synced, s.State));
    }

    [SkippableFact]
    public async Task PermanenteSemFoto_NaoEhEnviado()
    {
        Skip.IfNot(_pg.Available, _pg.SkipReason);
        var (service, gateway, db) = BuildService();
        await using var _ = db;

        var controller = NewController("Ala Central");
        var user = NewPermanentUser(NextCode());
        user.FacePhoto = null;
        user.Permissions.Add(new AccessPermission { ControllerId = controller.Id });
        db.Controllers.Add(controller);
        db.Users.Add(user);
        await db.SaveChangesAsync();

        await service.SyncUserAsync(user.Id);

        Assert.Empty(gateway.FaceUploads);
        Assert.Empty(await db.SyncStatuses.AsNoTracking().Where(s => s.UserId == user.Id).ToListAsync());
    }

    [SkippableFact]
    public async Task Visitante_EhCadastradoSemFace()
    {
        Skip.IfNot(_pg.Available, _pg.SkipReason);
        var (service, gateway, db) = BuildService();
        await using var _ = db;

        var controller = NewController("Quarto 101");
        var visitor = new User
        {
            UserCode = NextCode(),
            Name = "Acompanhante",
            Type = UserType.Visitor,
            ValidUntil = DateTime.UtcNow.AddDays(1),
        };
        visitor.Permissions.Add(new AccessPermission { ControllerId = controller.Id });
        db.Controllers.Add(controller);
        db.Users.Add(visitor);
        await db.SaveChangesAsync();

        await service.SyncUserAsync(visitor.Id);

        Assert.Single(gateway.FacelessUploads);
        Assert.Empty(gateway.FaceUploads);
        var status = await db.SyncStatuses.AsNoTracking().SingleAsync(s => s.UserId == visitor.Id);
        Assert.Equal(SyncState.Synced, status.State);
    }

    [SkippableFact]
    public async Task VisitanteSemValidade_NaoEhEnviado()
    {
        Skip.IfNot(_pg.Available, _pg.SkipReason);
        var (service, gateway, db) = BuildService();
        await using var _ = db;

        var controller = NewController("Quarto 102");
        var visitor = new User
        {
            UserCode = NextCode(),
            Name = "Sem validade",
            Type = UserType.Visitor,
            ValidUntil = null,
        };
        visitor.Permissions.Add(new AccessPermission { ControllerId = controller.Id });
        db.Controllers.Add(controller);
        db.Users.Add(visitor);
        await db.SaveChangesAsync();

        await service.SyncUserAsync(visitor.Id);

        Assert.Empty(gateway.FacelessUploads);
    }

    [SkippableFact]
    public async Task RevogacaoQueFalha_AgendaNovaTentativa()
    {
        Skip.IfNot(_pg.Available, _pg.SkipReason);
        var (service, gateway, db) = BuildService();
        await using var _ = db;

        var controller = NewController("Porta teimosa");
        var user = NewPermanentUser(NextCode());
        db.Controllers.Add(controller);
        db.Users.Add(user);
        db.SyncStatuses.Add(new DeviceSyncStatus
        {
            UserId = user.Id,
            ControllerId = controller.Id,
            State = SyncState.Synced,
        });
        await db.SaveChangesAsync();

        gateway.ThrowingOnDelete[controller.Id] = "aparelho inacessível";

        await service.RevokeUserAsync(user.Id);

        var status = await db.SyncStatuses.AsNoTracking().SingleAsync(s => s.UserId == user.Id);
        Assert.Equal(SyncState.Failed, status.State);
        // Revogação falha é SEMPRE transitória: precisa voltar, senão a credencial fica ativa.
        Assert.NotNull(status.NextRetryAtUtc);
    }

    [SkippableFact]
    public async Task ForceResync_LimpaOAparelhoEReenviaOsAtivos()
    {
        Skip.IfNot(_pg.Available, _pg.SkipReason);
        var (service, gateway, db) = BuildService();
        await using var _ = db;

        var controller = NewController("Porta para reset");
        var ativo = NewPermanentUser(NextCode(), "Ativo");
        var revogado = NewPermanentUser(NextCode(), "Revogado");
        revogado.RevokedAtUtc = DateTime.UtcNow;
        var semFoto = NewPermanentUser(NextCode(), "Sem foto");
        semFoto.FacePhoto = null;

        foreach (var u in new[] { ativo, revogado, semFoto })
            u.Permissions.Add(new AccessPermission { ControllerId = controller.Id });

        db.Controllers.Add(controller);
        db.Users.AddRange(ativo, revogado, semFoto);
        await db.SaveChangesAsync();

        await service.ForceResyncControllerAsync(controller.Id);

        Assert.Single(gateway.ClearedControllers);
        // Só o ativo com foto volta ao aparelho — revogado e sem foto ficam de fora.
        var upload = Assert.Single(gateway.FaceUploads);
        Assert.Equal(ativo.UserCode, upload.UserCode);
    }
}
