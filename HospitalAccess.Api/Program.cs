using System.Text;
using System.Text.Json.Serialization;
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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

// Enums como string no JSON (ex.: AccessMethod, AlarmKind, SyncState) — os DTOs do
// front-end Blazor já assumem essa representação.
builder.Services.AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

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

// Escuta de eventos em tempo real -> AccessLog (append-only).
builder.Services.AddHostedService<AccessEventRecorder>();
builder.Services.AddHostedService<AlarmEventRecorder>();

// Autenticação/autorização por papéis (admin, operador, recepção).
// Jwt:Key DEVE vir de secrets/config protegida (user-secrets em dev, env var/Key Vault em produção).
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.SectionName));
builder.Services.AddSingleton<JwtTokenService>();

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        var jwt = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwt.Issuer,
            ValidAudience = jwt.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Key)),
        };
    });
builder.Services.AddAuthorization();

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
        app.Logger.LogError(error, "Erro não tratado em {Method} {Path}", context.Request.Method, context.Request.Path);

        var isUniqueViolation = error is DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } };

        context.Response.StatusCode = isUniqueViolation ? StatusCodes.Status409Conflict : StatusCodes.Status500InternalServerError;
        context.Response.ContentType = "text/plain; charset=utf-8";
        await context.Response.WriteAsync(isUniqueViolation
            ? "Já existe um registro com esse valor único (ex.: número de série ou código já cadastrado)."
            // Ferramenta interna de uso administrativo (sempre atrás de autenticação) — expor a
            // mensagem da exceção ajuda o suporte on-premise sem depender de acesso aos logs do servidor.
            : $"Ocorreu um erro inesperado ao processar a solicitação: {error?.Message ?? "erro desconhecido"}");
    }));
}

// Serve a prova de conceito em React (HospitalAccess.Web.React, build em wwwroot/) no
// mesmo processo/porta da API — "npm run build" nesse projeto já aponta o outDir para cá.
// MapFallbackToFile depois de MapControllers: só assume rotas que nenhum endpoint de API
// bateu, para que o React Router funcione em refresh de uma rota tipo /controllers.
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapFallbackToFile("index.html");
app.Run();
