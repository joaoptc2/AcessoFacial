using System.Text;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using HospitalAccess.Api.Auth;
using HospitalAccess.Api.Services;
using HospitalAccess.Application.Qr;
using HospitalAccess.Application.Sync;
using HospitalAccess.Gateway;
using HospitalAccess.Gateway.Connections;
using HospitalAccess.Infrastructure.Persistence;
using HospitalAccess.Infrastructure.Qr;
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

// Proteção de dados: usada para criptografar em repouso a senha de comunicação dos
// controladores (ver AccessDbContext). As chaves são persistidas no diretório de dados do
// app; em produção com múltiplas instâncias, aponte para um store compartilhado (ver docs).
builder.Services.AddDataProtection();

// Banco (PostgreSQL). Connection string via config/secrets, nunca no código.
builder.Services.AddDbContext<AccessDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("Postgres")));

// Módulo QR (lógica pura de protocolo).
builder.Services.AddSingleton<QrAccessTokenService>();
builder.Services.AddSingleton<IQrImageEncoder, QrCoderImageEncoder>();

// Gateway de dispositivos (SDK DoNetDrive). Singleton: o ConnectorAllocator é único.
builder.Services.AddSingleton<ControllerConnectionFactory>();
builder.Services.AddSingleton<IDeviceGateway, DoNetDriveGateway>();

// Sincronização multi-controlador e expiração de visitantes.
builder.Services.AddScoped<IUserSyncService, UserSyncService>();
builder.Services.AddScoped<IVisitorExpirationJob, VisitorExpirationJob>();
builder.Services.AddHostedService<VisitorExpirationBackgroundService>();

// Reprocessa sincronizações pendentes/falhas (a "fila de retry" que antes nunca era executada).
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
