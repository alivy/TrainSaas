using Microsoft.EntityFrameworkCore;
using TrainSaas.Api.Auth;
using TrainSaas.Api.Domain;

namespace TrainSaas.Api.Data;

/// <summary>
/// Minimal startup seed: one platform admin (admin / initial password), one
/// demo institution and its admin staff account. Idempotent — each part is
/// only created when its table is still empty, so running the seed twice
/// leaves exactly one of each row.
/// </summary>
public static class SeedData
{
    public static async Task SeedMinimalAsync(AppDbContext db, IPasswordHasherService hasher)
    {
        if (!await db.PlatformUsers.AnyAsync())
        {
            db.PlatformUsers.Add(new PlatformUser
            {
                Username = "admin",
                PasswordHash = hasher.Hash(DefaultPassword.Initial),
            });
        }

        if (!await db.Institutions.AnyAsync())
        {
            var institution = new Institution
            {
                Code = "demo",
                Name = "演示机构",
                Status = InstitutionStatus.Active,
            };
            db.Institutions.Add(institution);
            await db.SaveChangesAsync();

            db.Staff.Add(new Staff
            {
                InstitutionId = institution.Id,
                Name = "机构管理员",
                Phone = "13900000001",
                PasswordHash = hasher.Hash(DefaultPassword.Initial),
                Role = StaffRole.Admin,
                IsActive = true,
            });
        }

        await db.SaveChangesAsync();
    }
}
