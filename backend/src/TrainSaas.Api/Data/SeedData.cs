using Microsoft.EntityFrameworkCore;
using TrainSaas.Api.Auth;
using TrainSaas.Api.Domain;

namespace TrainSaas.Api.Data;

/// <summary>
/// Startup seeds, both idempotent. <see cref="SeedMinimalAsync"/>: one platform
/// admin (admin / initial password), one demo institution and its admin staff
/// account (each part only created when its table is still empty).
/// <see cref="SeedDemoDataAsync"/>: the demo institution's full business
/// showcase, skipped via a key-based check when it was seeded before.
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

    /// <summary>
    /// Full demo seed (Task 16): extends <see cref="SeedMinimalAsync"/> (never
    /// replaces it) with the demo institution's business showcase — three extra
    /// staff (王小销/李老师/赵前台), two published products, three students
    /// (张小明+张小红 siblings sharing 13800001111, 李华), the 基础班 Tuesday and
    /// Thursday templates taught by 李老师, 张小明 enrolled in both, his confirmed
    /// order (fixed OrderNo, 24/24 pack, parent account 13800001111) and 李华's
    /// Pending order. All passwords are the initial one.
    ///
    /// Idempotent with a key-based check: skipped as soon as the demo institution
    /// has any staff beyond the seeded admin (13900000001), so restarts never
    /// duplicate rows. Runs at startup WITHOUT tenant context, therefore every
    /// ITenanted row sets InstitutionId explicitly (the SaveChanges auto-fill
    /// would throw) and every query uses IgnoreQueryFilters.
    /// </summary>
    public static async Task SeedDemoDataAsync(AppDbContext db, IPasswordHasherService hasher)
    {
        var institution = await db.Institutions.SingleOrDefaultAsync(i => i.Code == "demo");
        var admin = institution is null
            ? null
            : await db.Staff.IgnoreQueryFilters().SingleOrDefaultAsync(
                s => s.InstitutionId == institution.Id && s.Phone == "13900000001");
        if (institution is null || admin is null)
        {
            return; // SeedMinimalAsync creates both under the same gate; nothing to attach to.
        }

        // Key-based idempotency: the demo institution already has staff beyond
        // the admin → the demo seed ran before.
        if (await db.Staff.IgnoreQueryFilters()
                .AnyAsync(s => s.InstitutionId == institution.Id && s.Phone != "13900000001"))
        {
            return;
        }

        var password = hasher.Hash(DefaultPassword.Initial);
        var sales = new Staff
        {
            InstitutionId = institution.Id,
            Name = "王小销",
            Phone = "13900000002",
            PasswordHash = password,
            Role = StaffRole.Sales,
            IsActive = true,
        };
        var teacher = new Staff
        {
            InstitutionId = institution.Id,
            Name = "李老师",
            Phone = "13900000003",
            PasswordHash = password,
            Role = StaffRole.Teacher,
            IsActive = true,
        };
        var frontDesk = new Staff
        {
            InstitutionId = institution.Id,
            Name = "赵前台",
            Phone = "13900000004",
            PasswordHash = password,
            Role = StaffRole.FrontDesk,
            IsActive = true,
        };
        db.Staff.AddRange(sales, teacher, frontDesk);

        var dance = new Product
        {
            InstitutionId = institution.Id,
            Name = "少儿舞蹈基础班",
            Type = ProductType.Fixed,
            Price = 1980m,
            SessionCount = 24,
            IsPublished = true,
        };
        var trial = new Product
        {
            InstitutionId = institution.Id,
            Name = "体能体验课",
            Type = ProductType.Flexible,
            Price = 299m,
            SessionCount = 10,
            IsPublished = true,
        };
        db.Products.AddRange(dance, trial);
        await db.SaveChangesAsync(); // staff + product ids

        // 张小明/张小红 are siblings sharing the parent's phone; all three
        // students belong to sales 王小销.
        var ming = new Student
        {
            InstitutionId = institution.Id,
            Name = "张小明",
            Phone = "13800001111",
            AssignedSalesId = sales.Id,
            IsActive = true,
        };
        var hong = new Student
        {
            InstitutionId = institution.Id,
            Name = "张小红",
            Phone = "13800001111",
            AssignedSalesId = sales.Id,
            IsActive = true,
        };
        var lihua = new Student
        {
            InstitutionId = institution.Id,
            Name = "李华",
            Phone = "13800002222",
            AssignedSalesId = sales.Id,
            IsActive = true,
        };
        db.Students.AddRange(ming, hong, lihua);
        await db.SaveChangesAsync(); // student ids

        var tuesday = new ClassTemplate
        {
            InstitutionId = institution.Id,
            ProductId = dance.Id,
            DayOfWeek = DayOfWeek.Tuesday,
            StartTime = new TimeOnly(18, 0),
            EndTime = new TimeOnly(19, 30),
            TeacherId = teacher.Id,
            Capacity = 20,
            IsActive = true,
        };
        var thursday = new ClassTemplate
        {
            InstitutionId = institution.Id,
            ProductId = dance.Id,
            DayOfWeek = DayOfWeek.Thursday,
            StartTime = new TimeOnly(18, 0),
            EndTime = new TimeOnly(19, 30),
            TeacherId = teacher.Id,
            Capacity = 20,
            IsActive = true,
        };
        db.ClassTemplates.AddRange(tuesday, thursday);
        await db.SaveChangesAsync(); // template ids

        db.ClassEnrollments.AddRange(
            new ClassEnrollment
            {
                InstitutionId = institution.Id,
                TemplateId = tuesday.Id,
                StudentId = ming.Id,
                EnrolledById = admin.Id,
                Status = EnrollmentStatus.Enrolled,
            },
            new ClassEnrollment
            {
                InstitutionId = institution.Id,
                TemplateId = thursday.Id,
                StudentId = ming.Id,
                EnrolledById = admin.Id,
                Status = EnrollmentStatus.Enrolled,
            });

        // 张小明's confirmed order is written directly (OrderService must not run
        // at startup): Status=Active + ConfirmedById/At, plus its 24/24 pack and
        // the parent account — the same rows confirm-over-API would produce.
        var confirmedAt = new DateTime(2026, 1, 1, 10, 0, 0);
        var mingOrder = new Order
        {
            InstitutionId = institution.Id,
            OrderNo = "SO20260101000001",
            StudentId = ming.Id,
            ProductId = dance.Id,
            Amount = 1980m,
            SalesId = sales.Id,
            Status = OrderStatus.Active,
            CreatedById = admin.Id,
            ConfirmedById = admin.Id,
            ConfirmedAt = confirmedAt,
            CreatedAt = confirmedAt.AddHours(-1),
        };
        var lihuaOrder = new Order
        {
            InstitutionId = institution.Id,
            OrderNo = "SO20260101000002",
            StudentId = lihua.Id,
            ProductId = trial.Id,
            Amount = 299m,
            SalesId = sales.Id,
            Status = OrderStatus.Pending,
            CreatedById = admin.Id,
            CreatedAt = confirmedAt,
        };
        db.Orders.AddRange(mingOrder, lihuaOrder);
        await db.SaveChangesAsync(); // order ids

        db.SessionPacks.Add(new SessionPack
        {
            InstitutionId = institution.Id,
            OrderId = mingOrder.Id,
            StudentId = ming.Id,
            ProductId = dance.Id,
            TotalSessions = 24,
            RemainingSessions = 24,
            Status = PackStatus.Active,
            CreatedAt = confirmedAt,
        });

        // ParentAccount is global by phone (unique index): key-based guard.
        if (!await db.ParentAccounts.AnyAsync(p => p.Phone == "13800001111"))
        {
            db.ParentAccounts.Add(new ParentAccount
            {
                Phone = "13800001111",
                PasswordHash = password,
                CreatedAt = confirmedAt,
            });
        }

        await db.SaveChangesAsync();
    }
}
