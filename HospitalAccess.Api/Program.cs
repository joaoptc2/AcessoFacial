using System.Text;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using HospitalAccess.Api.Auth;
using HospitalAccess.Api.Services;
using HospitalAccess.Application.Qr;
using HospitalAccess.Application.Sync;
using HospitalAccess.Gateway;
using HospitalAccess.Gateway.Connections;
using HospitalAccess.Infrastructure.Devices;
using HospitalAccess.Infrastructure.Persistence;
using HospitalAccess.Infrastructure.Qr;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

// Enums como string no JSON (ex.: AccessMethod, AlarmKind, SyncState) — os tipos TS do
// front-end React já assumem essa representação.
builder.Services.AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Proteção de dados: criptografa em repouso as senhas dos controladores (CommunicationPassword,
// ApiPassword — ver AccessDbContext). O CHAVEIRO PRECISA SER PERSISTIDO num diretório FIXO: sem
// isso (o default guarda em local volátil/efêmero), um restart ou redeploy gera chaves novas e as
// senhas já cifradas ficam INDECIFRÁVEIS — o SDK então rejeita a senha com "Password Is Error".
// SetApplicationName fixo mantém o mesmo escopo de proteção entre reinícios/instâncias.
// O diretório precisa ser gravável pelo usuário do serviço e entrar no backup.
var dpKeysPath = builder.Configuration["DataProtection:KeysPath"];
if (string.IsNullOrWhiteSpace(dpKeysPath)) dpKeysPath = "/var/lib/hospitalaccess/dpkeys";
try
{
    Directory.CreateDirectory(dpKeysPath);
    // Sonda de escrita: se o diretório NÃO for gravável pelo usuário do serviço, a criptografia de
    // segredos (Protect) só falharia na hora de SALVAR uma senha — virando um 500 opaco. Detectar
    // aqui, no boot, transforma isso numa mensagem clara no log.
    var probe = Path.Combine(dpKeysPath, ".write-probe");
    File.WriteAllText(probe, "ok");
    File.Delete(probe);
}
catch (Exception ex)
{
    Console.Error.WriteLine(
        $"[DataProtection] ATENÇÃO: o diretório de chaves '{dpKeysPath}' não é gravável ({ex.GetType().Name}: {ex.Message}).\n" +
        "  Consequência: SALVAR a senha de um controlador falha com erro 500 (não dá para criptografar) e o chaveiro não persiste.\n" +
        "  Correção: aponte DataProtection__KeysPath para um diretório gravável e persistente, OU dê posse do diretório ao usuário do serviço.\n" +
        $"  Ex.: sudo install -d -o <usuario-do-servico> -g <grupo> {dpKeysPath}");
}
builder.Services.AddDataProtection()
    .SetApplicationName("HospitalAccess")
    .PersistKeysToFileSystem(new DirectoryInfo(dpKeysPath));

// Banco (PostgreSQL). Connection string via config/secrets, nunca no código.
// Fail-fast: sem a connection string o app subiria e só quebraria no primeiro acesso ao
// banco (seeder), com um stack cru "The ConnectionString property has not been initialized".
var postgresConnectionString = builder.Configuration.GetConnectionString("Postgres");
if (string.IsNullOrWhiteSpace(postgresConnectionString))
{
    throw new InvalidOperationException(
        "ConnectionStrings:Postgres não configurada. Defina via user-secrets (dev, carregam só em " +
        "ASPNETCORE_ENVIRONMENT=Development) ou via variável de ambiente ConnectionStrings__Postgres " +
        "(produção). Ver README e appsettings.example.json.");
}
builder.Services.AddDbContext<AccessDbContext>(o => o.UseNpgsql(postgresConnectionString));

// Módulo QR (lógica pura de protocolo).
builder.Services.AddSingleton<QrAccessTokenService>();
builder.Services.AddSingleton<IQrImageEncoder, QrCoderImageEncoder>();

// Fuso horário dos controladores: a validade (Expiry) é gravada no aparelho em horário LOCAL.
// Configurável em Device:TimeZone (IANA, ex.: "America/Sao_Paulo"); padrão Brasil (UTC-3).
var deviceTimeZoneId = builder.Configuration["Device:TimeZone"] ?? "America/Sao_Paulo";
TimeZoneInfo deviceTimeZone;
try
{
    deviceTimeZone = TimeZoneInfo.FindSystemTimeZoneById(deviceTimeZoneId);
}
catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
{
    // Fallback seguro: fuso fixo UTC-3, para não derrubar o app se o id não existir no host.
    deviceTimeZone = TimeZoneInfo.CreateCustomTimeZone("DeviceTZ", TimeSpan.FromHours(-3), "DeviceTZ", "DeviceTZ");
}
builder.Services.AddSingleton(deviceTimeZone);

// Gateway de dispositivos (SDK DoNetDrive). Singleton: o ConnectorAllocator é único.
builder.Services.AddSingleton<ControllerConnectionFactory>();
builder.Services.AddSingleton<IDeviceGateway, DoNetDriveGateway>();

// Integração HTTP com o painel web dos controladores (para LER o QRCode que o aparelho gera e,
// opcionalmente, provisionar via /api/People/New). Senha padrão global em Device:DefaultApiPassword.
builder.Services.Configure<DeviceHttpOptions>(builder.Configuration.GetSection(DeviceHttpOptions.SectionName));
builder.Services.AddSingleton<DeviceHttpClientFactory>();

// Leitura/provisão do QR real via API HTTP do controlador (o QR que o aparelho valida).
builder.Services.AddScoped<DeviceQrService>();

// Propaga as portas padrão do grupo organizacional para os membros (base + extras manuais).
builder.Services.AddScoped<GroupAccessService>();

// Sincronização multi-controlador e expiração de visitantes.
builder.Services.AddScoped<IUserSyncService, UserSyncService>();
builder.Services.AddScoped<IVisitorExpirationJob, VisitorExpirationJob>();
builder.Services.AddHostedService<VisitorExpirationBackgroundService>();

// Fila de sincronização: processa os cadastros/edições UM DE CADA VEZ ("por partes"), evitando o
// CommandStatus_Timeout que ocorria com vários AddPersonAndImage concorrentes no mesmo controlador.
builder.Services.AddSingleton<UserSyncQueue>();
builder.Services.AddSingleton<IUserSyncQueue>(sp => sp.GetRequiredService<UserSyncQueue>());
builder.Services.AddHostedService<UserSyncQueueWorker>();

// Reprocessa sincronizações pendentes/falhas: reenfileira na fila serial (não processa em paralelo).
builder.Services.AddHostedService<SyncRetryBackgroundService>();

// Ativa e mantém o monitoramento em tempo real (BeginWatch) em todos os controladores — sem isto
// o 8190H não empurra eventos (default OFF, não persiste após reboot; protocolo §10).
builder.Services.AddHostedService<DeviceMonitoringBackgroundService>();

// Coleta de retaguarda dos registros offline (recupera eventos ocorridos com o servidor fora do ar).
builder.Services.AddHostedService<OfflineRecordCollectorBackgroundService>();

// Heartbeat/health-check dos controladores (alimenta o painel de status).
builder.Services.AddHostedService<DeviceHealthBackgroundService>();

// Expurgo de dados conforme a política de retenção (LGPD, ver tela de Configurações).
builder.Services.AddHostedService<DataRetentionBackgroundService>();

// Escuta de eventos em tempo real -> AccessLog (append-only).
builder.Services.AddHostedService<AccessEventRecorder>();
builder.Services.AddHostedService<AlarmEventRecorder>();

// Autenticação/autorização por papéis (admin, operador, recepção).
// Jwt:Key DEVE vir de secrets/config protegida (user-secrets em dev, env var/Key Vault em produção).
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.SectionName));
builder.Services.AddSingleton<JwtTokenService>();

var jwtOptions = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();

// Fail-fast: HS256 exige chave de pelo menos 256 bits (32 bytes). Uma chave ausente/curta só
// falharia no primeiro login (ou, pior, validaria tokens com chave fraca) — barrar já na subida.
if (Encoding.UTF8.GetByteCount(jwtOptions.Key) < 32)
{
    throw new InvalidOperationException(
        "Jwt:Key ausente ou curta demais. Configure uma chave de pelo menos 32 bytes " +
        "(user-secrets em dev, variável de ambiente/cofre em produção). Ver README.");
}

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtOptions.Issuer,
            ValidAudience = jwtOptions.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.Key)),
            ClockSkew = TimeSpan.FromSeconds(30),
        };
    });
builder.Services.AddAuthorization();

// Rate limiting: protege o login contra brute force de senha de staff. Janela fixa por IP,
// pequena o suficiente para travar tentativas automatizadas sem atrapalhar o uso normal.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("login", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AccessDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
    await StaffUserSeeder.SeedAsync(db, builder.Configuration, logger);
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
    app.UseDeveloperExceptionPage();
}
else
{
    // Sem isto, uma exceção não tratada em produção resulta em resposta 500 com corpo
    // vazio (comportamento padrão do ASP.NET Core) — o front-end então exibe uma caixa
    // vermelha sem nenhuma mensagem, já que só sabe mostrar response.Content.
    app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
    {
        var error = context.Features.Get<IExceptionHandlerFeature>()?.Error;
        // ID de correlação: vai para o log com a exceção completa e é devolvido ao cliente, para o
        // suporte cruzar sem que a mensagem/stack da exceção seja exposta na resposta.
        var correlationId = context.TraceIdentifier;
        app.Logger.LogError(error, "Erro não tratado ({CorrelationId}) em {Method} {Path}",
            correlationId, context.Request.Method, context.Request.Path);

        var isUniqueViolation = error is DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } };
        var isConcurrency = error is DbUpdateConcurrencyException;

        context.Response.StatusCode = isUniqueViolation ? StatusCodes.Status409Conflict
            : isConcurrency ? StatusCodes.Status409Conflict
            : StatusCodes.Status500InternalServerError;
        context.Response.ContentType = "text/plain; charset=utf-8";
        await context.Response.WriteAsync(isUniqueViolation
            ? "Já existe um registro com esse valor único (ex.: número de série ou código já cadastrado)."
            : isConcurrency
                ? "O registro foi alterado por outra pessoa enquanto você editava. Recarregue e tente novamente."
                : $"Ocorreu um erro inesperado ao processar a solicitação. Código de referência: {correlationId}.");
    }));
}

// Serve o front-end React (HospitalAccess.Web.React, build em wwwroot/) no mesmo
// processo/porta da API — "npm run build" nesse projeto já aponta o outDir para cá.
// MapFallbackToFile depois de MapControllers: só assume rotas que nenhum endpoint de API
// bateu, para que o React Router funcione em refresh de uma rota tipo /controllers.
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapFallbackToFile("index.html");
app.Run();
