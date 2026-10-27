using Microsoft.EntityFrameworkCore;
using TrainSaas.Api.Domain;

namespace TrainSaas.Tests.Unit;

public class TenantFilterTests
{
    [Fact]
    public async Task Query_as_institution_1_returns_only_institution_1_rows()
    {
        var (db, conn) = TestDb.Create(new FakeCurrentUser { UserType = UserType.Staff, InstitutionId = 1 });
        db.Students.Add(new Student { InstitutionId = 1, Name = "A学生", Phone = "13800000001" });
        db.Students.Add(new Student { InstitutionId = 2, Name = "B学生", Phone = "13800000002" });
        await db.SaveChangesAsync();
        var names = await db.Students.Select(s => s.Name).ToListAsync();
        Assert.Equal(["A学生"], names);
    }

    [Fact]
    public async Task Anonymous_or_parent_sees_no_tenanted_rows()
    {
        var (db, conn) = TestDb.Create(new FakeCurrentUser { UserType = UserType.Parent, Phone = "13800000001" });
        db.Students.Add(new Student { InstitutionId = 1, Name = "A学生", Phone = "13800000001" });
        await db.SaveChangesAsync();
        Assert.Empty(await db.Students.ToListAsync());
    }

    [Fact]
    public async Task SaveChanges_auto_fills_InstitutionId_from_current_user()
    {
        var (db, conn) = TestDb.Create(new FakeCurrentUser { UserType = UserType.Staff, InstitutionId = 7 });
        var s = new Student { Name = "自动归属", Phone = "13800000003" };
        db.Students.Add(s); await db.SaveChangesAsync();
        Assert.Equal(7, s.InstitutionId);
    }
}
