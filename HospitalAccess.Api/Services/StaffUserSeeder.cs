using HospitalAccess.Domain.Entities;
using HospitalAccess.Domain.Enums;
using HospitalAccess.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace HospitalAccess.Api.Services;

/// <summary>
/// Bootstrap: cria o primeiro StaffUser (admin) se nenhum existir, a partir de
/// configuração/secrets (Seed:AdminUsername / Seed:AdminPassword). Sem isso, ninguém
/// consegue logar na primeira execução. Troque a senha imediatamente após o primeiro login
/// — este seeder não impede reuso da senha padrão.
/// </summary>
public static class StaffUserSeeder
{
    public static async Task SeedAsync(AccessDbContext db, IConfiguration config, ILogger logger)
    {
        if (await db.StaffUsers.AnyAsync()) return;

        var username = config["Seed:AdminUsername"] ?? "admin";
        var password = config["Seed:AdminPassword"];
        if (string.IsNullOrWhiteSpace(password))
        {
            logger.LogWarning(
                "Nenhum StaffUser existe e Seed:AdminPassword não foi configurado (secrets/env). " +
                "Login inicial não será possível até configurar e reiniciar.");
            return;
        }

        var admin = new StaffUser { Username = username, Role = StaffRole.Admin };
        admin.PasswordHash = new PasswordHasher<StaffUser>().HashPassword(admin, password);

        db.StaffUsers.Add(admin);
        await db.SaveChangesAsync();
        logger.LogWarning("StaffUser admin '{Username}' criado via seed. Troque a senha assim que possível.", username);
    }
}
