using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TrainSaas.Api.Auth;
using TrainSaas.Api.Data;
using TrainSaas.Api.Domain;
using TrainSaas.Api.Dtos;
using TrainSaas.Api.Errors;
using TrainSaas.Api.Services;

namespace TrainSaas.Tests.Unit;

/// <summary>
/// AttendanceService 单元测试（TestDb 内存 SQLite + FakeCurrentUser）——点名
/// 状态机（账务心脏）：
/// <list type="bullet">
/// <item>无→Present：点名 + RollCall 扣减流水 + 包余量-1 + 课次 Completed。</item>
/// <item>无→Leave/Absent：只写点名，绝不扣课。</item>
/// <item>Present→Present：无操作（不重复扣，Review Focus 2）。</item>
/// <item>Present→Leave/Absent：写 Direction=-1 冲正行（ReversesConsumptionId
/// 指向原行、Reason="点名修正"）并回补包余量；原行一字不动（append-only）；
/// 已冲正再改只改标记。</item>
/// <item>资格：在班或持有该产品 Active 且有余的包，否则单项 Ok=false
/// "不在名单且无可用课时包"且不落点名行；逐项部分成功互不影响。</item>
/// <item>守卫：空名单 400；名单内重复学员 400；非 Admin 非授课教师 403；
/// 未知课次 404。</item>
/// </list>
/// </summary>
public class AttendanceServiceTests
{
    /// <summary>播种用的上下文用户（机构 1 管理员）：只决定租户与自动填机构。</summary>
    private static readonly FakeCurrentUser SeedUser = new()
    {
        UserType = UserType.Staff,
        InstitutionId = 1,
        StaffId = 0,
        Role = StaffRole.Admin,
        Phone = "13900000001",
        Name = "播种管理员",
    };

    private sealed record Fixture(
        AppDbContext Db,
        AttendanceService Attendance,
        SqliteConnection Conn,
        Staff Teacher,
        Staff OtherTeacher,
        Staff Sales,
        Staff Admin,
        Product Product,
        ClassTemplate Template,
        ClassSession Session);

    private static FakeCurrentUser AsCaller(Staff staff) => new()
    {
        UserType = UserType.Staff,
        InstitutionId = staff.InstitutionId,
        StaffId = staff.Id,
        Role = staff.Role,
        Phone = staff.Phone,
        Name = staff.Name,
    };

    private static async Task<Fixture> CreateFixtureAsync()
    {
        var (db, conn) = TestDb.Create(SeedUser);
        var teacher = new Staff
        {
            InstitutionId = 1, Name = "授课教师", Phone = "13911113001",
            PasswordHash = "not-a-real-hash", Role = StaffRole.Teacher, IsActive = true,
        };
        var otherTeacher = new Staff
        {
            InstitutionId = 1, Name = "其他教师", Phone = "13911113002",
            PasswordHash = "not-a-real-hash", Role = StaffRole.Teacher, IsActive = true,
        };
        var sales = new Staff
        {
            InstitutionId = 1, Name = "销售", Phone = "13911113003",
            PasswordHash = "not-a-real-hash", Role = StaffRole.Sales, IsActive = true,
        };
        var admin = new Staff
        {
            InstitutionId = 1, Name = "管理员", Phone = "13911113004",
            PasswordHash = "not-a-real-hash", Role = StaffRole.Admin, IsActive = true,
        };
        db.Staff.AddRange(teacher, otherTeacher, sales, admin);
        var product = new Product
        {
            InstitutionId = 1, Name = "少儿英语固定班", Type = ProductType.Fixed,
            Price = 1980m, SessionCount = 10,
        };
        db.Products.Add(product);
        await db.SaveChangesAsync();
        var template = new ClassTemplate
        {
            InstitutionId = 1, ProductId = product.Id, DayOfWeek = DayOfWeek.Monday,
            StartTime = new TimeOnly(18, 0), EndTime = new TimeOnly(19, 30),
            TeacherId = teacher.Id, IsActive = true,
        };
        db.ClassTemplates.Add(template);
        await db.SaveChangesAsync();
        var session = new ClassSession
        {
            InstitutionId = 1, TemplateId = template.Id, TeacherId = teacher.Id,
            StartAt = new DateTime(2026, 11, 2, 18, 0, 0),
            EndAt = new DateTime(2026, 11, 2, 19, 30, 0),
            Status = SessionStatus.Scheduled,
        };
        db.ClassSessions.Add(session);
        await db.SaveChangesAsync();

        var attendance = new AttendanceService(db, new DeductionService(db));
        return new Fixture(db, attendance, conn, teacher, otherTeacher, sales, admin, product, template, session);
    }

    private static async Task<Student> SeedStudentAsync(AppDbContext db, string phone)
    {
        var student = new Student
        {
            InstitutionId = 1, Name = $"学员{phone}", Phone = phone, IsActive = true,
        };
        db.Students.Add(student);
        await db.SaveChangesAsync();
        return student;
    }

    private static async Task EnrollAsync(AppDbContext db, int templateId, int studentId)
    {
        db.ClassEnrollments.Add(new ClassEnrollment
        {
            InstitutionId = 1,
            TemplateId = templateId,
            StudentId = studentId,
            EnrolledById = 1,
            Status = EnrollmentStatus.Enrolled,
        });
        await db.SaveChangesAsync();
    }

    private static async Task<SessionPack> SeedPackAsync(
        AppDbContext db, int studentId, int productId, int orderId, int remainingSessions)
    {
        var pack = new SessionPack
        {
            InstitutionId = 1,
            OrderId = orderId,
            StudentId = studentId,
            ProductId = productId,
            TotalSessions = Math.Max(remainingSessions, 1),
            RemainingSessions = remainingSessions,
            Status = PackStatus.Active,
        };
        db.SessionPacks.Add(pack);
        await db.SaveChangesAsync();
        return pack;
    }

    private static Task<AttendanceResult> MarkPresentAsync(Fixture f, Staff caller, params int[] studentIds) =>
        f.Attendance.MarkAsync(
            f.Session.Id,
            studentIds.Select(id => new MarkItem(id, AttendanceMark.Present)).ToList(),
            AsCaller(caller));

    [Fact]
    public async Task Present_creates_attendance_and_consumption_and_decrements_remaining()
    {
        var f = await CreateFixtureAsync();
        var student = await SeedStudentAsync(f.Db, "13811114001");
        await EnrollAsync(f.Db, f.Template.Id, student.Id);
        var pack = await SeedPackAsync(f.Db, student.Id, f.Product.Id, 701, 5);

        var result = await f.Attendance.MarkAsync(
            f.Session.Id, [new MarkItem(student.Id, AttendanceMark.Present)], AsCaller(f.Teacher));

        var item = Assert.Single(result.Results);
        Assert.Equal(student.Id, item.StudentId);
        Assert.True(item.Ok);
        Assert.Null(item.Error);

        // 点名行：Present、操作人=授课教师、租户自动填充。
        var row = await f.Db.Attendances.AsNoTracking()
            .SingleAsync(a => a.ClassSessionId == f.Session.Id && a.StudentId == student.Id);
        Assert.Equal(AttendanceMark.Present, row.Mark);
        Assert.Equal(f.Teacher.Id, row.OperatorId);
        Assert.Equal(1, row.InstitutionId);

        // 扣减流水：Direction=+1 RollCall 指向该包与该课次。
        var consumption = Assert.Single(await f.Db.Consumptions.AsNoTracking().ToListAsync());
        Assert.Equal(student.Id, consumption.StudentId);
        Assert.Equal(pack.Id, consumption.SessionPackId);
        Assert.Equal(f.Session.Id, consumption.ClassSessionId);
        Assert.Equal(ConsumptionKind.RollCall, consumption.Kind);
        Assert.Equal(1, consumption.Direction);
        Assert.Equal(f.Teacher.Id, consumption.OperatorId);
        Assert.Null(consumption.Reason);
        Assert.Null(consumption.ReversesConsumptionId);
        Assert.Equal(1, consumption.InstitutionId);

        // 包余量 5→4；任一点名后课次 Completed。
        Assert.Equal(4, (await f.Db.SessionPacks.AsNoTracking().SingleAsync(p => p.Id == pack.Id)).RemainingSessions);
        Assert.Equal(SessionStatus.Completed,
            (await f.Db.ClassSessions.AsNoTracking().SingleAsync(s => s.Id == f.Session.Id)).Status);
    }

    [Fact]
    public async Task Leave_and_absent_do_not_deduct()
    {
        var f = await CreateFixtureAsync();
        var student1 = await SeedStudentAsync(f.Db, "13811114002");
        var student2 = await SeedStudentAsync(f.Db, "13811114003");
        await EnrollAsync(f.Db, f.Template.Id, student1.Id);
        await EnrollAsync(f.Db, f.Template.Id, student2.Id);

        var result = await f.Attendance.MarkAsync(
            f.Session.Id,
            [
                new MarkItem(student1.Id, AttendanceMark.Leave),
                new MarkItem(student2.Id, AttendanceMark.Absent),
            ],
            AsCaller(f.Teacher));

        Assert.All(result.Results, r => Assert.True(r.Ok));
        var marks = await f.Db.Attendances.AsNoTracking()
            .Where(a => a.ClassSessionId == f.Session.Id)
            .ToDictionaryAsync(a => a.StudentId);
        Assert.Equal(AttendanceMark.Leave, marks[student1.Id].Mark);
        Assert.Equal(AttendanceMark.Absent, marks[student2.Id].Mark);

        // 无任何课时流水；但课次因存在点名行转为 Completed。
        Assert.Empty(await f.Db.Consumptions.AsNoTracking().ToListAsync());
        Assert.Equal(SessionStatus.Completed,
            (await f.Db.ClassSessions.AsNoTracking().SingleAsync(s => s.Id == f.Session.Id)).Status);
    }

    [Fact]
    public async Task Remark_present_to_present_does_not_double_deduct()
    {
        var f = await CreateFixtureAsync();
        var student = await SeedStudentAsync(f.Db, "13811114004");
        await EnrollAsync(f.Db, f.Template.Id, student.Id);
        var pack = await SeedPackAsync(f.Db, student.Id, f.Product.Id, 702, 5);

        await MarkPresentAsync(f, f.Teacher, student.Id);
        var again = await MarkPresentAsync(f, f.Teacher, student.Id);

        // Review Focus 2：重复点名 Present 是幂等无操作。
        var item = Assert.Single(again.Results);
        Assert.True(item.Ok);
        Assert.Equal(4, (await f.Db.SessionPacks.AsNoTracking().SingleAsync(p => p.Id == pack.Id)).RemainingSessions);
        Assert.Single(await f.Db.Consumptions.AsNoTracking().ToListAsync());
        Assert.Single(await f.Db.Attendances.AsNoTracking().ToListAsync());

        // Completed 幂等：第二次调用不报错，状态保持。
        Assert.Equal(SessionStatus.Completed,
            (await f.Db.ClassSessions.AsNoTracking().SingleAsync(s => s.Id == f.Session.Id)).Status);
    }

    [Fact]
    public async Task Remark_present_to_absent_creates_reversal_and_restores_remaining_original_row_untouched()
    {
        var f = await CreateFixtureAsync();
        var student = await SeedStudentAsync(f.Db, "13811114005");
        await EnrollAsync(f.Db, f.Template.Id, student.Id);
        var pack = await SeedPackAsync(f.Db, student.Id, f.Product.Id, 703, 5);

        await MarkPresentAsync(f, f.Teacher, student.Id);
        var original = await f.Db.Consumptions.AsNoTracking()
            .SingleAsync(c => c.Direction == 1);
        var originalCreatedAt = original.CreatedAt;

        // 管理员修正为 Absent（同时覆盖"Admin 可点名他人课次"）。
        var result = await f.Attendance.MarkAsync(
            f.Session.Id, [new MarkItem(student.Id, AttendanceMark.Absent)], AsCaller(f.Admin));

        Assert.True(Assert.Single(result.Results).Ok);

        // 点名行翻到 Absent，操作人=管理员。
        var row = await f.Db.Attendances.AsNoTracking()
            .SingleAsync(a => a.ClassSessionId == f.Session.Id && a.StudentId == student.Id);
        Assert.Equal(AttendanceMark.Absent, row.Mark);
        Assert.Equal(f.Admin.Id, row.OperatorId);

        // 包余量回补 4→5。
        Assert.Equal(5, (await f.Db.SessionPacks.AsNoTracking().SingleAsync(p => p.Id == pack.Id)).RemainingSessions);

        // 冲正行：Direction=-1、Kind=Reversal、Reason="点名修正"、指向原行、
        // 同包/同学员/同课次、操作人=管理员。
        var rows = await f.Db.Consumptions.AsNoTracking().OrderBy(c => c.Id).ToListAsync();
        Assert.Equal(2, rows.Count);
        var reversal = rows.Single(c => c.Direction == -1);
        Assert.Equal(ConsumptionKind.Reversal, reversal.Kind);
        Assert.Equal("点名修正", reversal.Reason);
        Assert.Equal(original.Id, reversal.ReversesConsumptionId);
        Assert.Equal(original.SessionPackId, reversal.SessionPackId);
        Assert.Equal(student.Id, reversal.StudentId);
        Assert.Equal(f.Session.Id, reversal.ClassSessionId);
        Assert.Equal(f.Admin.Id, reversal.OperatorId);

        // 原行 append-only：一字不动。
        var untouched = rows.Single(c => c.Direction == 1);
        Assert.Equal(original.Id, untouched.Id);
        Assert.Equal(ConsumptionKind.RollCall, untouched.Kind);
        Assert.Null(untouched.Reason);
        Assert.Null(untouched.ReversesConsumptionId);
        Assert.Equal(originalCreatedAt, untouched.CreatedAt);
        Assert.Equal(f.Teacher.Id, untouched.OperatorId);

        // 已冲正再改 Absent：无第二条冲正，只改标记。
        await f.Attendance.MarkAsync(
            f.Session.Id, [new MarkItem(student.Id, AttendanceMark.Absent)], AsCaller(f.Admin));
        Assert.Equal(2, await f.Db.Consumptions.AsNoTracking().CountAsync());
        Assert.Equal(5, (await f.Db.SessionPacks.AsNoTracking().SingleAsync(p => p.Id == pack.Id)).RemainingSessions);

        // 修正后再点名 Present：重新扣一次（新 +1 流水，未被旧冲正波及）。
        await MarkPresentAsync(f, f.Teacher, student.Id);
        var latest = await f.Db.Consumptions.AsNoTracking()
            .Where(c => c.Direction == 1).OrderByDescending(c => c.Id).FirstAsync();
        Assert.NotEqual(original.Id, latest.Id);
        Assert.Null(latest.ReversesConsumptionId);
        Assert.Equal(4, (await f.Db.SessionPacks.AsNoTracking().SingleAsync(p => p.Id == pack.Id)).RemainingSessions);
        Assert.Equal(3, await f.Db.Consumptions.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task Adhoc_student_with_pack_succeeds_without_pack_reports_error_and_others_still_succeed()
    {
        var f = await CreateFixtureAsync();
        var enrolled = await SeedStudentAsync(f.Db, "13811114006");
        await EnrollAsync(f.Db, f.Template.Id, enrolled.Id);
        await SeedPackAsync(f.Db, enrolled.Id, f.Product.Id, 704, 5);

        // 名单外临时学员：持有该产品 Active 且有余的包 → 可点名扣课。
        var adhoc = await SeedStudentAsync(f.Db, "13811114007");
        var adhocPack = await SeedPackAsync(f.Db, adhoc.Id, f.Product.Id, 705, 3);

        // 名单外且无可用包 → 单项失败，不影响同批其他学员。
        var stranger = await SeedStudentAsync(f.Db, "13811114008");

        var result = await f.Attendance.MarkAsync(
            f.Session.Id,
            [
                new MarkItem(stranger.Id, AttendanceMark.Present),
                new MarkItem(enrolled.Id, AttendanceMark.Present),
                new MarkItem(adhoc.Id, AttendanceMark.Present),
            ],
            AsCaller(f.Teacher));

        // 逐项部分成功：失败学员在前也不阻断后面的成功。
        Assert.Equal(3, result.Results.Count);
        var strangerResult = result.Results.Single(r => r.StudentId == stranger.Id);
        Assert.False(strangerResult.Ok);
        Assert.Equal("不在名单且无可用课时包", strangerResult.Error);
        Assert.True(result.Results.Single(r => r.StudentId == enrolled.Id).Ok);
        Assert.True(result.Results.Single(r => r.StudentId == adhoc.Id).Ok);

        // 失败学员不落点名行；另两名各一行。
        Assert.Equal(2, await f.Db.Attendances.AsNoTracking()
            .CountAsync(a => a.ClassSessionId == f.Session.Id));
        Assert.False(await f.Db.Attendances.AsNoTracking()
            .AnyAsync(a => a.ClassSessionId == f.Session.Id && a.StudentId == stranger.Id));

        // 恰两条 +1 流水：在班学员扣自己的包，临时学员扣自己的包。
        var consumptions = await f.Db.Consumptions.AsNoTracking().ToListAsync();
        Assert.Equal(2, consumptions.Count);
        Assert.All(consumptions, c => Assert.Equal(1, c.Direction));
        Assert.Equal(4, (await f.Db.SessionPacks.AsNoTracking()
            .SingleAsync(p => p.StudentId == enrolled.Id)).RemainingSessions);
        Assert.Equal(2, (await f.Db.SessionPacks.AsNoTracking()
            .SingleAsync(p => p.Id == adhocPack.Id)).RemainingSessions);
    }

    [Fact]
    public async Task Empty_marks_throws_BusinessRule()
    {
        var f = await CreateFixtureAsync();
        var student = await SeedStudentAsync(f.Db, "13811114009");
        await EnrollAsync(f.Db, f.Template.Id, student.Id);

        // 空名单 / null 名单一律 400，不落任何行、课次保持 Scheduled。
        await Assert.ThrowsAsync<BusinessRuleException>(
            () => f.Attendance.MarkAsync(f.Session.Id, [], AsCaller(f.Teacher)));
        await Assert.ThrowsAsync<BusinessRuleException>(
            () => f.Attendance.MarkAsync(f.Session.Id, null!, AsCaller(f.Teacher)));

        Assert.Empty(await f.Db.Attendances.AsNoTracking().ToListAsync());
        Assert.Empty(await f.Db.Consumptions.AsNoTracking().ToListAsync());
        Assert.Equal(SessionStatus.Scheduled,
            (await f.Db.ClassSessions.AsNoTracking().SingleAsync(s => s.Id == f.Session.Id)).Status);
    }

    [Fact]
    public async Task Duplicate_studentIds_in_marks_throw_BusinessRule()
    {
        var f = await CreateFixtureAsync();
        var student = await SeedStudentAsync(f.Db, "13811114010");
        await EnrollAsync(f.Db, f.Template.Id, student.Id);
        await SeedPackAsync(f.Db, student.Id, f.Product.Id, 706, 5);

        await Assert.ThrowsAsync<BusinessRuleException>(() => f.Attendance.MarkAsync(
            f.Session.Id,
            [
                new MarkItem(student.Id, AttendanceMark.Present),
                new MarkItem(student.Id, AttendanceMark.Leave),
            ],
            AsCaller(f.Teacher)));

        // 校验失败不落任何行、不扣课。
        Assert.Empty(await f.Db.Attendances.AsNoTracking().ToListAsync());
        Assert.Empty(await f.Db.Consumptions.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Other_teacher_marking_returns_Forbidden_admin_allowed()
    {
        var f = await CreateFixtureAsync();
        var student = await SeedStudentAsync(f.Db, "13811114011");
        await EnrollAsync(f.Db, f.Template.Id, student.Id);
        await SeedPackAsync(f.Db, student.Id, f.Product.Id, 707, 5);
        var marks = new List<MarkItem> { new(student.Id, AttendanceMark.Present) };

        // 其他教师（非本课次授课人）：403，不落行。
        await Assert.ThrowsAsync<ForbiddenActionException>(
            () => f.Attendance.MarkAsync(f.Session.Id, marks, AsCaller(f.OtherTeacher)));

        // 销售/前台（既非 Admin 也非授课教师）：同样 403。
        await Assert.ThrowsAsync<ForbiddenActionException>(
            () => f.Attendance.MarkAsync(f.Session.Id, marks, AsCaller(f.Sales)));

        Assert.Empty(await f.Db.Attendances.AsNoTracking().ToListAsync());
        Assert.Empty(await f.Db.Consumptions.AsNoTracking().ToListAsync());

        // 管理员（非授课教师）放行。
        var result = await f.Attendance.MarkAsync(f.Session.Id, marks, AsCaller(f.Admin));
        Assert.True(Assert.Single(result.Results).Ok);
        Assert.Single(await f.Db.Attendances.AsNoTracking().ToListAsync());

        // 未知课次 → 404（租户过滤器内与不存在同态，不泄露存在性）。
        await Assert.ThrowsAsync<NotFoundException>(
            () => f.Attendance.MarkAsync(999999, marks, AsCaller(f.Admin)));
        await Assert.ThrowsAsync<NotFoundException>(
            () => f.Attendance.MarkAsync(999999, marks, AsCaller(f.Teacher)));
    }

    [Fact]
    public async Task Enrolled_student_without_pack_reports_no_available_pack()
    {
        var f = await CreateFixtureAsync();
        // 在班学员但没有任何课时包：资格通过（在班），扣课失败 → 该生
        // Ok=false "无可用课时包"且不落点名行。
        var student = await SeedStudentAsync(f.Db, "13811114012");
        await EnrollAsync(f.Db, f.Template.Id, student.Id);
        var withPack = await SeedStudentAsync(f.Db, "13811114013");
        await EnrollAsync(f.Db, f.Template.Id, withPack.Id);
        await SeedPackAsync(f.Db, withPack.Id, f.Product.Id, 708, 2);

        var result = await f.Attendance.MarkAsync(
            f.Session.Id,
            [
                new MarkItem(student.Id, AttendanceMark.Present),
                new MarkItem(withPack.Id, AttendanceMark.Present),
            ],
            AsCaller(f.Teacher));

        var failed = result.Results.Single(r => r.StudentId == student.Id);
        Assert.False(failed.Ok);
        Assert.Equal("无可用课时包", failed.Error);
        Assert.True(result.Results.Single(r => r.StudentId == withPack.Id).Ok);

        // 扣课失败的学员不写点名行（仅另一个学员的一行）。
        var rows = await f.Db.Attendances.AsNoTracking().ToListAsync();
        var row = Assert.Single(rows);
        Assert.Equal(withPack.Id, row.StudentId);
        Assert.Single(await f.Db.Consumptions.AsNoTracking().ToListAsync());
    }
}
