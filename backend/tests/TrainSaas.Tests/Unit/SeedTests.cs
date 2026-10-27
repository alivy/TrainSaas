using Microsoft.EntityFrameworkCore;
using TrainSaas.Api.Auth;
using TrainSaas.Api.Data;
using TrainSaas.Api.Domain;

namespace TrainSaas.Tests.Unit;

public class SeedTests
{
    [Fact]
    public async Task Seed_Minimal_Creates_platform_admin_and_demo_institution_idempotently()
    {
        var (db, conn) = TestDb.Create();
        var hasher = new PasswordHasherService();

        await SeedData.SeedMinimalAsync(db, hasher);
        await SeedData.SeedMinimalAsync(db, hasher);

        Assert.Equal(1, await db.PlatformUsers.CountAsync());
        var admin = await db.PlatformUsers.SingleAsync();
        Assert.Equal("admin", admin.Username);
        Assert.True(hasher.Verify(admin.PasswordHash, DefaultPassword.Initial));

        Assert.Equal(1, await db.Institutions.CountAsync());
        var institution = await db.Institutions.SingleAsync();
        Assert.Equal("demo", institution.Code);

        var staff = await db.Staff.IgnoreQueryFilters().SingleAsync(s => s.Phone == "13900000001");
        Assert.Equal(StaffRole.Admin, staff.Role);
        Assert.Equal(institution.Id, staff.InstitutionId);
    }
}
