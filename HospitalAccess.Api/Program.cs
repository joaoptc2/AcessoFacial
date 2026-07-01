using System.Text;
using HospitalAccess.Api.Auth;
using HospitalAccess.Api.Services;
using HospitalAccess.Application.Qr;
using HospitalAccess.Application.Sync;
using HospitalAccess.Gateway;
using HospitalAccess.Gateway.Connections;
using HospitalAccess.Infrastructure.Persistence;
using HospitalAccess.Infrastructure.Qr;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
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
}

app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.Run();
