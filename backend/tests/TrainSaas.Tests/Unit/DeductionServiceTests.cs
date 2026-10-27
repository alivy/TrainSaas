using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TrainSaas.Api.Auth;
using TrainSaas.Api.Data;
using TrainSaas.Api.Domain;
using TrainSaas.Api.Errors;
using TrainSaas.Api.Services;

namespace TrainSaas.Tests.Unit;

/// <summary>
/// DeductionService 单元测试（TestDb 内存 SQLite + FakeCurrentUser）——账务
/// 心脏的选包与扣减规则：
/// <list type="bullet">
/// <item>选包：同产品按 (CreatedAt, Id) 升序取最早；跨产品包绝不动；
/// 全部为空/不可用 → NoAvailablePackException("无可用课时包")；Voided 包
/// 被跳过（Review Focus 3）。</item>
/// <item>落流水：每次扣减恰好一行 Direction=+1 的 Consumption（含
/// SessionPackId/ClassSessionId/Kind/OperatorId/Reason），扣的是哪个包
/// 流水就指向哪个包。</item>
/// <item>并发（R10/R13）：两份 DbContext（各自连接同一临时文件库）对
/// RemainingSessions=1 的包真并发扣减——恰一个成功、另一个
/// NoAvailablePackException，RemainingSessions 永不为负、该包恰好一条
/// 扣减流水。</item>
/// </list>
/// </summary>
public class DeductionServiceTests
{
    /// <summary>当前用户：机构 1 的管理员（StaffId=1）。</summary>
    private readonly FakeCurrentUser _user = new()
    {
        UserType = UserType.Staff,
        InstitutionId = 1,
        StaffId = 1,
        Role = StaffRole.Admin,
        Phone = "13900000001",
        Name = "管理员",
    };

    private (AppDbContext Db, IDeductionService Deduction, SqliteConnection Conn) Create()
    {
        var (db, conn) = TestDb.Create(_user);
        return (db, new DeductionService(db), conn);
    }

    private static async Task<Student> SeedStudentAsync(
        AppDbContext db, string phone, int institutionId = 1)
    {
        var student = new Student
        {
            InstitutionId = institutionId,
            Name = $"学员{phone}",
            Phone = phone,
            IsActive = true,
        };
        db.Students.Add(student);
        await db.SaveChangesAsync();
        return student;
    }

    private static async Task<Product> SeedProductAsync(AppDbContext db, string name, int institutionId = 1)
    {
        var product = new Product
        {
            InstitutionId = institutionId,
            Name = name,
            Type = ProductType.Flexible,
            Price = 1200m,
            SessionCount = 10,
        };
        db.Products.Add(product);
        await db.SaveChangesAsync();
        return product;
    }

    private static async Task<SessionPack> SeedPackAsync(
        AppDbContext db,
        int studentId,
        int productId,
        int orderId,
        int remainingSessions,
        PackStatus status = PackStatus.Active,
        DateTime? createdAt = null,
        int institutionId = 1)
    {
        var pack = new SessionPack
        {
            InstitutionId = institutionId,
            OrderId = orderId,
            StudentId = studentId,
            ProductId = productId,
            TotalSessions = Math.Max(remainingSessions, 1),
            RemainingSessions = remainingSessions,
            Status = status,
            CreatedAt = createdAt ?? DateTime.Now,
        };
        db.SessionPacks.Add(pack);
        await db.SaveChangesAsync();
        return pack;
    }

    [Fact]
    public async Task Deduct_consumes_earliest_pack_first_same_product()
    {
        var (db, deduction, conn) = Create();
        var student = await SeedStudentAsync(db, "13811112001");
        var product = await SeedProductAsync(db, "课程包");
        var early = await SeedPackAsync(db, student.Id, product.Id, 101, 5,
            createdAt: new DateTime(2026, 10, 1, 10, 0, 0));
        var late = await SeedPackAsync(db, student.Id, product.Id, 102, 5,
            createdAt: new DateTime(2026, 10, 5, 10, 0, 0));

        var first = await deduction.DeductAsync(student.Id, product.Id, null, ConsumptionKind.RollCall, 1);
        var second = await deduction.DeductAsync(student.Id, product.Id, null, ConsumptionKind.RollCall, 1);

        // 两次都扣 (CreatedAt,Id) 升序最早的包，较晚的包纹丝不动。
        Assert.Equal(early.Id, first.SessionPackId);
        Assert.Equal(early.Id, second.SessionPackId);
        Assert.Equal(3, (await db.SessionPacks.AsNoTracking().SingleAsync(p => p.Id == early.Id)).RemainingSessions);
        Assert.Equal(5, (await db.SessionPacks.AsNoTracking().SingleAsync(p => p.Id == late.Id)).RemainingSessions);

        // 两行 +1 流水都指向早包。
        var consumptions = await db.Consumptions.AsNoTracking().ToListAsync();
        Assert.Equal(2, consumptions.Count);
        Assert.All(consumptions, c => Assert.Equal(early.Id, c.SessionPackId));
        Assert.All(consumptions, c => Assert.Equal(1, c.Direction));
        Assert.All(consumptions, c => Assert.Equal(1, c.InstitutionId));
    }

    [Fact]
    public async Task Deduct_never_touches_other_product_pack()
    {
        var (db, deduction, conn) = Create();
        var student = await SeedStudentAsync(db, "13811112002");
        var productA = await SeedProductAsync(db, "产品甲");
        var productB = await SeedProductAsync(db, "产品乙");
        var packA = await SeedPackAsync(db, student.Id, productA.Id, 201, 5);
        var packB = await SeedPackAsync(db, student.Id, productB.Id, 202, 5);

        var consumption = await deduction.DeductAsync(
            student.Id, productA.Id, classSessionId: 77,
            ConsumptionKind.RollCall, operatorId: 2, reason: "人工扣课");

        // 扣的是甲的包；乙的包不受影响。
        Assert.Equal(packA.Id, consumption.SessionPackId);
        Assert.Equal(4, (await db.SessionPacks.AsNoTracking().SingleAsync(p => p.Id == packA.Id)).RemainingSessions);
        Assert.Equal(5, (await db.SessionPacks.AsNoTracking().SingleAsync(p => p.Id == packB.Id)).RemainingSessions);

        // 流水形态：Direction=+1、关联课次、Kind、操作人、原因各就各位。
        var row = Assert.Single(await db.Consumptions.AsNoTracking().ToListAsync());
        Assert.Equal(student.Id, row.StudentId);
        Assert.Equal(77, row.ClassSessionId);
        Assert.Equal(ConsumptionKind.RollCall, row.Kind);
        Assert.Equal(1, row.Direction);
        Assert.Equal(2, row.OperatorId);
        Assert.Equal("人工扣课", row.Reason);
        Assert.Null(row.ReversesConsumptionId);
    }

    [Fact]
    public async Task Deduct_with_all_packs_empty_throws_NoAvailablePack()
    {
        var (db, deduction, conn) = Create();
        var student = await SeedStudentAsync(db, "13811112003");
        var product = await SeedProductAsync(db, "课程包");
        await SeedPackAsync(db, student.Id, product.Id, 301, 0);

        var ex = await Assert.ThrowsAsync<NoAvailablePackException>(
            () => deduction.DeductAsync(student.Id, product.Id, null, ConsumptionKind.RollCall, 1));
        Assert.Equal("无可用课时包", ex.Message);

        // 没有任何包（从未购课）同样 400。
        var other = await SeedStudentAsync(db, "13811112004");
        await Assert.ThrowsAsync<NoAvailablePackException>(
            () => deduction.DeductAsync(other.Id, product.Id, null, ConsumptionKind.RollCall, 1));

        // 跨机构包经租户过滤器不可见：与本机构无包同态，不泄露存在性。
        var awayStudent = await SeedStudentAsync(db, "13811112005", institutionId: 2);
        await SeedPackAsync(db, awayStudent.Id, product.Id, 302, 5, institutionId: 2);
        await Assert.ThrowsAsync<NoAvailablePackException>(
            () => deduction.DeductAsync(awayStudent.Id, product.Id, null, ConsumptionKind.RollCall, 1));

        // 失败不落任何流水。
        Assert.Empty(await db.Consumptions.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task Deduct_skips_voided_pack()
    {
        var (db, deduction, conn) = Create();
        var student = await SeedStudentAsync(db, "13811112006");
        var product = await SeedProductAsync(db, "课程包");
        // 作废包有余量但不可扣：跳过它扣下一个 Active 包。
        await SeedPackAsync(db, student.Id, product.Id, 401, 5,
            status: PackStatus.Voided, createdAt: new DateTime(2026, 10, 1));
        var active = await SeedPackAsync(db, student.Id, product.Id, 402, 3,
            createdAt: new DateTime(2026, 10, 8));

        var consumption = await deduction.DeductAsync(student.Id, product.Id, null, ConsumptionKind.RollCall, 1);

        Assert.Equal(active.Id, consumption.SessionPackId);
        Assert.Equal(2, (await db.SessionPacks.AsNoTracking().SingleAsync(p => p.Id == active.Id)).RemainingSessions);

        // 只有作废包时视为无可用包。
        var other = await SeedStudentAsync(db, "13811112007");
        await SeedPackAsync(db, other.Id, product.Id, 403, 9, status: PackStatus.Voided);
        await Assert.ThrowsAsync<NoAvailablePackException>(
            () => deduction.DeductAsync(other.Id, product.Id, null, ConsumptionKind.RollCall, 1));
    }

    /// <summary>
    /// R10/R13 并发不变量：两份独立 DbContext（各自连接同一临时文件 SQLite）
    /// 对 RemainingSessions=1 的同一包真并发（Task.WhenAll）扣减。CAS 守卫
    /// （WHERE RemainingSessions&gt;0）保证：恰一个成功、另一个
    /// NoAvailablePackException；RemainingSessions 终值 0（永不为负）；该包
    /// 恰好一条 Direction=+1 流水。
    /// </summary>
    [Fact]
    public async Task Concurrent_deduct_of_last_session_never_goes_negative()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"trainsaas-deduct-{Guid.NewGuid():N}.db");
        try
        {
            var options1 = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={dbPath}").Options;
            var user1 = new FakeCurrentUser
            {
                UserType = UserType.Staff, InstitutionId = 1, StaffId = 1,
                Role = StaffRole.Admin, Phone = "13900000001", Name = "管理员一",
            };
            var user2 = new FakeCurrentUser
            {
                UserType = UserType.Staff, InstitutionId = 1, StaffId = 2,
                Role = StaffRole.Admin, Phone = "13900000002", Name = "管理员二",
            };

            Consumption? first = null;
            Consumption? second = null;
            Exception? firstError = null;
            Exception? secondError = null;

            using (var db1 = new AppDbContext(options1, user1))
            {
                db1.Database.EnsureCreated();
                var student = new Student
                {
                    InstitutionId = 1, Name = "学员", Phone = "13844441001", IsActive = true,
                };
                var product = new Product
                {
                    InstitutionId = 1, Name = "课程包", Type = ProductType.Flexible,
                    Price = 1200m, SessionCount = 10,
                };
                db1.Students.Add(student);
                db1.Products.Add(product);
                await db1.SaveChangesAsync();
                var pack = new SessionPack
                {
                    InstitutionId = 1, OrderId = 601, StudentId = student.Id,
                    ProductId = product.Id, TotalSessions = 1, RemainingSessions = 1,
                    Status = PackStatus.Active,
                };
                db1.SessionPacks.Add(pack);
                await db1.SaveChangesAsync();

                // 第二个作用域：同库不同连接/上下文/用户（并行请求）。
                var options2 = new DbContextOptionsBuilder<AppDbContext>()
                    .UseSqlite($"Data Source={dbPath}").Options;
                using var db2 = new AppDbContext(options2, user2);
                var deduction1 = new DeductionService(db1);
                var deduction2 = new DeductionService(db2);

                await Task.WhenAll(
                    Task.Run(async () =>
                    {
                        try
                        {
                            first = await deduction1.DeductAsync(
                                student.Id, product.Id, null, ConsumptionKind.RollCall, 1);
                        }
                        catch (Exception ex)
                        {
                            firstError = ex;
                        }
                    }),
                    Task.Run(async () =>
                    {
                        try
                        {
                            second = await deduction2.DeductAsync(
                                student.Id, product.Id, null, ConsumptionKind.RollCall, 2);
                        }
                        catch (Exception ex)
                        {
                            secondError = ex;
                        }
                    }));

                // 恰一个成功；输家的异常必须是 NoAvailablePackException（绝不能
                // 是 500 级的 Sqlite/DbUpdate 异常，也不允许双双成功）。
                Assert.True((first is null) != (second is null),
                    $"恰应一个成功：first={(first is null ? "失败" : "成功")}, second={(second is null ? "失败" : "成功")}");
                var loserError = first is null ? firstError : secondError;
                Assert.IsType<NoAvailablePackException>(loserError);
                Assert.Equal("无可用课时包", loserError!.Message);

                // 终态：RemainingSessions == 0（永不为负），该包恰好一条 +1 流水。
                var finalPack = await db1.SessionPacks.AsNoTracking()
                    .SingleAsync(p => p.Id == pack.Id);
                Assert.Equal(0, finalPack.RemainingSessions);
                Assert.True(finalPack.RemainingSessions >= 0);
                var consumptions = await db1.Consumptions.AsNoTracking()
                    .Where(c => c.SessionPackId == pack.Id).ToListAsync();
                var deductionRow = Assert.Single(consumptions);
                Assert.Equal(1, deductionRow.Direction);
                Assert.Equal(ConsumptionKind.RollCall, deductionRow.Kind);
            }
        }
        finally
        {
            try
            {
                File.Delete(dbPath);
            }
            catch (IOException)
            {
            }
        }
    }
}
