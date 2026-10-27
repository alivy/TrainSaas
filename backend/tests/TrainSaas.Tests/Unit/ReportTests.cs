using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TrainSaas.Api.Auth;
using TrainSaas.Api.Data;
using TrainSaas.Api.Domain;
using TrainSaas.Api.Errors;
using TrainSaas.Api.Services;

namespace TrainSaas.Tests.Unit;

/// <summary>
/// ReportService 单元测试（TestDb 内存 SQLite + FakeCurrentUser）——销售业绩
/// 与教师课量两张报表的口径：
/// <list type="bullet">
/// <item>销售：只统计 Status=Active 且 ConfirmedAt 落在 [from 当日零点,
/// to 次日零点) 的订单；Pending/Cancelled/Voided 与窗口外的确认一律不计；
/// 金额在 ToListAsync 之后内存 GroupBy/Sum（Global Constraints：decimal
/// 不进 SQL 聚合）；窗口内无成交的销售不出行（YAGNI，不补零行）。</item>
/// <item>教师：completedSessions = 窗口内 Status=Completed 的课次实例数；
/// presentMarks = 课次落在同一窗口内的 Present 点名行数（Leave/Absent 不计）。</item>
/// <item>角色闸门：销售报表 Admin 全员 / Sales 仅自己一行 / 其他角色 403；
/// 教师报表 Admin 全员 / Teacher 仅自己一行 / 其他角色 403。</item>
/// <item>租户隔离：两张报表都在员工令牌下运行，走租户过滤器——他机构的
/// 订单/课次/点名绝不进入本机构报表（家长端之外没有 IgnoreQueryFilters）。</item>
/// </list>
/// </summary>
public class ReportTests
{
    /// <summary>报表窗口：2026-10-01 ~ 2026-10-31（含两端当日）。</summary>
    private static readonly DateTime From = new(2026, 10, 1);
    private static readonly DateTime To = new(2026, 10, 31);

    private static FakeCurrentUser StaffUser(int staffId, StaffRole role, int institutionId = 1) => new()
    {
        UserType = UserType.Staff,
        InstitutionId = institutionId,
        StaffId = staffId,
        Role = role,
        Phone = $"139{staffId:D8}",
        Name = $"员工{staffId}",
    };

    private static (AppDbContext Db, ReportService Service, SqliteConnection Conn) Create(
        ICurrentUser user)
    {
        var (db, conn) = TestDb.Create(user);
        return (db, new ReportService(db), conn);
    }

    private static async Task<Staff> SeedStaffAsync(
        AppDbContext db, string name, string phone, StaffRole role, int institutionId = 1)
    {
        var staff = new Staff
        {
            InstitutionId = institutionId,
            Name = name,
            Phone = phone,
            PasswordHash = "not-a-real-hash",
            Role = role,
            IsActive = true,
        };
        db.Staff.Add(staff);
        await db.SaveChangesAsync();
        return staff;
    }

    private static async Task<Order> SeedOrderAsync(
        AppDbContext db,
        string orderNo,
        int? salesId,
        decimal amount,
        OrderStatus status,
        DateTime? confirmedAt,
        int institutionId = 1)
    {
        var order = new Order
        {
            InstitutionId = institutionId,
            OrderNo = orderNo,
            StudentId = 700 + institutionId,
            ProductId = 800 + institutionId,
            Amount = amount,
            SalesId = salesId,
            Status = status,
            CreatedById = salesId ?? 1,
            ConfirmedAt = confirmedAt,
        };
        db.Orders.Add(order);
        await db.SaveChangesAsync();
        return order;
    }

    private static async Task<ClassSession> SeedSessionAsync(
        AppDbContext db,
        int teacherId,
        DateTime startAt,
        SessionStatus status,
        int institutionId = 1)
    {
        var session = new ClassSession
        {
            InstitutionId = institutionId,
            TemplateId = 900 + institutionId,
            TeacherId = teacherId,
            StartAt = startAt,
            EndAt = startAt.AddMinutes(90),
            Status = status,
        };
        db.ClassSessions.Add(session);
        await db.SaveChangesAsync();
        return session;
    }

    private static async Task SeedAttendanceAsync(
        AppDbContext db, int classSessionId, int studentId, AttendanceMark mark, int institutionId = 1)
    {
        db.Attendances.Add(new Attendance
        {
            InstitutionId = institutionId,
            ClassSessionId = classSessionId,
            StudentId = studentId,
            Mark = mark,
            OperatorId = 1,
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Sales_report_sums_confirmed_orders_only_and_excludes_cancelled_voided()
    {
        var admin = StaffUser(1, StaffRole.Admin);
        var (db, service, conn) = Create(admin);
        var salesA = await SeedStaffAsync(db, "销售甲", "13810001001", StaffRole.Sales);
        var salesB = await SeedStaffAsync(db, "销售乙", "13810001002", StaffRole.Sales);
        // 窗口内零成交的销售：不出行（不补零行）。
        var salesC = await SeedStaffAsync(db, "销售丙", "13810001003", StaffRole.Sales);

        // 计入：A 两单、B 三单（含 from 当日零点与 to 当日末尾两个边界）。
        await SeedOrderAsync(db, "SO0001", salesA.Id, 1980.50m, OrderStatus.Active, new DateTime(2026, 10, 5, 10, 0, 0));
        await SeedOrderAsync(db, "SO0002", salesA.Id, 999.50m, OrderStatus.Active, new DateTime(2026, 10, 20, 9, 30, 0));
        await SeedOrderAsync(db, "SO0003", salesB.Id, 300m, OrderStatus.Active, new DateTime(2026, 10, 10, 12, 0, 0));
        await SeedOrderAsync(db, "SO0004", salesB.Id, 50m, OrderStatus.Active, new DateTime(2026, 10, 1, 0, 0, 0));
        await SeedOrderAsync(db, "SO0005", salesB.Id, 25.25m, OrderStatus.Active, new DateTime(2026, 10, 31, 23, 59, 59));

        // 不计入：未确认（Pending/Cancelled 无 ConfirmedAt）、已作废（虽在窗口内确认过）、
        // 窗口外（前一日 23:59:59 与次日零点整）、无归属销售。
        await SeedOrderAsync(db, "SO0006", salesA.Id, 500m, OrderStatus.Pending, null);
        await SeedOrderAsync(db, "SO0007", salesA.Id, 700m, OrderStatus.Cancelled, null);
        await SeedOrderAsync(db, "SO0008", salesB.Id, 800m, OrderStatus.Voided, new DateTime(2026, 10, 11, 15, 0, 0));
        await SeedOrderAsync(db, "SO0009", salesA.Id, 100m, OrderStatus.Active, new DateTime(2026, 9, 30, 23, 59, 59));
        await SeedOrderAsync(db, "SO0010", salesA.Id, 200m, OrderStatus.Active, new DateTime(2026, 11, 1, 0, 0, 0));
        await SeedOrderAsync(db, "SO0011", null, 60m, OrderStatus.Active, new DateTime(2026, 10, 15, 8, 0, 0));
        // 他机构同窗口订单：租户过滤器必须挡住（报表绝不 IgnoreQueryFilters）。
        await SeedOrderAsync(db, "SO0012", salesA.Id, 9999m, OrderStatus.Active,
            new DateTime(2026, 10, 6, 10, 0, 0), institutionId: 2);

        var rows = await service.SalesAsync(From, To, admin);

        // 只有窗口内有成交的 A、B 两行，按 staffId 升序。
        Assert.Equal(2, rows.Count);
        Assert.Equal(salesA.Id, rows[0].StaffId);
        Assert.Equal("销售甲", rows[0].StaffName);
        Assert.Equal(2, rows[0].OrderCount);
        Assert.Equal(2980.00m, rows[0].TotalAmount);
        Assert.Equal(salesB.Id, rows[1].StaffId);
        Assert.Equal("销售乙", rows[1].StaffName);
        Assert.Equal(3, rows[1].OrderCount);
        Assert.Equal(375.25m, rows[1].TotalAmount);
        Assert.DoesNotContain(rows, r => r.StaffId == salesC.Id);

        // 窗口收窄到单日（10-31）→ 只剩 B 一行一单：边界随窗口移动。
        var shifted = await service.SalesAsync(
            new DateTime(2026, 10, 31), new DateTime(2026, 10, 31), admin);
        var only = Assert.Single(shifted);
        Assert.Equal(salesB.Id, only.StaffId);
        Assert.Equal(1, only.OrderCount);
        Assert.Equal(25.25m, only.TotalAmount);

        conn.Dispose();
    }

    [Fact]
    public async Task Teacher_report_counts_completed_sessions_and_present_marks()
    {
        var admin = StaffUser(1, StaffRole.Admin);
        var (db, service, conn) = Create(admin);
        var teacherA = await SeedStaffAsync(db, "教师甲", "13810002001", StaffRole.Teacher);
        var teacherB = await SeedStaffAsync(db, "教师乙", "13810002002", StaffRole.Teacher);
        // 窗口内既无已完成课次也无点名的教师：不出行。
        var teacherC = await SeedStaffAsync(db, "教师丙", "13810002003", StaffRole.Teacher);

        // 甲：2 个 Completed 在窗内（另 Scheduled/Cancelled 各一，及窗外两个 Completed）。
        var a1 = await SeedSessionAsync(db, teacherA.Id, new DateTime(2026, 10, 5, 18, 0, 0), SessionStatus.Completed);
        var a2 = await SeedSessionAsync(db, teacherA.Id, new DateTime(2026, 10, 12, 18, 0, 0), SessionStatus.Completed);
        await SeedSessionAsync(db, teacherA.Id, new DateTime(2026, 10, 19, 18, 0, 0), SessionStatus.Scheduled);
        await SeedSessionAsync(db, teacherA.Id, new DateTime(2026, 10, 26, 18, 0, 0), SessionStatus.Cancelled);
        await SeedSessionAsync(db, teacherA.Id, new DateTime(2026, 9, 30, 23, 59, 59), SessionStatus.Completed);
        await SeedSessionAsync(db, teacherA.Id, new DateTime(2026, 11, 1, 0, 0, 0), SessionStatus.Completed);

        // 乙：2 个 Completed 在窗内（含 from 当日零点边界），其中一个无点名。
        var b1 = await SeedSessionAsync(db, teacherB.Id, new DateTime(2026, 10, 6, 18, 0, 0), SessionStatus.Completed);
        await SeedSessionAsync(db, teacherB.Id, new DateTime(2026, 10, 1, 0, 0, 0), SessionStatus.Completed);

        // 点名：甲 3 个 Present（请假/缺勤不计），乙 3 个 Present；窗外课次的 Present 不计。
        await SeedAttendanceAsync(db, a1.Id, 501, AttendanceMark.Present);
        await SeedAttendanceAsync(db, a1.Id, 502, AttendanceMark.Present);
        await SeedAttendanceAsync(db, a1.Id, 503, AttendanceMark.Leave);
        await SeedAttendanceAsync(db, a2.Id, 501, AttendanceMark.Present);
        await SeedAttendanceAsync(db, a2.Id, 502, AttendanceMark.Absent);
        await SeedAttendanceAsync(db, b1.Id, 501, AttendanceMark.Present);
        await SeedAttendanceAsync(db, b1.Id, 502, AttendanceMark.Present);
        await SeedAttendanceAsync(db, b1.Id, 503, AttendanceMark.Present);
        var outOfWindow = await SeedSessionAsync(
            db, teacherA.Id, new DateTime(2026, 9, 20, 18, 0, 0), SessionStatus.Completed);
        await SeedAttendanceAsync(db, outOfWindow.Id, 501, AttendanceMark.Present);
        // 他机构课次与点名：租户过滤器必须挡住。
        var otherOrg = await SeedSessionAsync(
            db, teacherA.Id, new DateTime(2026, 10, 7, 18, 0, 0), SessionStatus.Completed, institutionId: 2);
        await SeedAttendanceAsync(db, otherOrg.Id, 501, AttendanceMark.Present, institutionId: 2);

        var rows = await service.TeachersAsync(From, To, admin);

        Assert.Equal(2, rows.Count);
        Assert.Equal(teacherA.Id, rows[0].TeacherId);
        Assert.Equal("教师甲", rows[0].TeacherName);
        Assert.Equal(2, rows[0].CompletedSessions);
        Assert.Equal(3, rows[0].PresentMarks);
        Assert.Equal(teacherB.Id, rows[1].TeacherId);
        Assert.Equal("教师乙", rows[1].TeacherName);
        Assert.Equal(2, rows[1].CompletedSessions);
        Assert.Equal(3, rows[1].PresentMarks);
        Assert.DoesNotContain(rows, r => r.TeacherId == teacherC.Id);

        // 教师本人令牌：只见自己那一行（甲）。
        var own = Assert.Single(await service.TeachersAsync(From, To, StaffUser(teacherA.Id, StaffRole.Teacher)));
        Assert.Equal(teacherA.Id, own.TeacherId);
        Assert.Equal(2, own.CompletedSessions);
        Assert.Equal(3, own.PresentMarks);

        conn.Dispose();
    }

    [Fact]
    public async Task Sales_reporter_with_sales_role_sees_only_own_row_and_others_get_403()
    {
        var (db, service, conn) = Create(StaffUser(1, StaffRole.Admin));
        var salesA = await SeedStaffAsync(db, "销售甲", "13810003001", StaffRole.Sales);
        var salesB = await SeedStaffAsync(db, "销售乙", "13810003002", StaffRole.Sales);
        await SeedOrderAsync(db, "SO0021", salesA.Id, 1000m, OrderStatus.Active, new DateTime(2026, 10, 5, 10, 0, 0));
        await SeedOrderAsync(db, "SO0022", salesA.Id, 500.5m, OrderStatus.Active, new DateTime(2026, 10, 6, 10, 0, 0));
        await SeedOrderAsync(db, "SO0023", salesB.Id, 777m, OrderStatus.Active, new DateTime(2026, 10, 7, 10, 0, 0));

        // Sales 令牌：只有本人一行，别人的成交不泄露。
        var rows = await service.SalesAsync(From, To, StaffUser(salesA.Id, StaffRole.Sales));
        var own = Assert.Single(rows);
        Assert.Equal(salesA.Id, own.StaffId);
        Assert.Equal("销售甲", own.StaffName);
        Assert.Equal(2, own.OrderCount);
        Assert.Equal(1500.5m, own.TotalAmount);

        // 窗口内零成交的销售本人：空数组（不补零行）。
        var quietSales = await SeedStaffAsync(db, "销售丁", "13810003004", StaffRole.Sales);
        Assert.Empty(await service.SalesAsync(From, To, StaffUser(quietSales.Id, StaffRole.Sales)));

        // 其他角色一律 403（Teacher/FrontDesk）。
        await Assert.ThrowsAsync<ForbiddenActionException>(
            () => service.SalesAsync(From, To, StaffUser(9, StaffRole.Teacher)));
        await Assert.ThrowsAsync<ForbiddenActionException>(
            () => service.SalesAsync(From, To, StaffUser(9, StaffRole.FrontDesk)));

        conn.Dispose();
    }

    [Fact]
    public async Task Teacher_reporter_sees_only_own_row_and_other_roles_get_403()
    {
        var (db, service, conn) = Create(StaffUser(1, StaffRole.Admin));
        var teacherA = await SeedStaffAsync(db, "教师甲", "13810004001", StaffRole.Teacher);
        var teacherB = await SeedStaffAsync(db, "教师乙", "13810004002", StaffRole.Teacher);
        var a1 = await SeedSessionAsync(db, teacherA.Id, new DateTime(2026, 10, 5, 18, 0, 0), SessionStatus.Completed);
        await SeedAttendanceAsync(db, a1.Id, 501, AttendanceMark.Present);
        await SeedSessionAsync(db, teacherB.Id, new DateTime(2026, 10, 6, 18, 0, 0), SessionStatus.Completed);

        // Teacher 令牌：只有本人一行。
        var own = Assert.Single(await service.TeachersAsync(From, To, StaffUser(teacherA.Id, StaffRole.Teacher)));
        Assert.Equal(teacherA.Id, own.TeacherId);
        Assert.Equal(1, own.CompletedSessions);
        Assert.Equal(1, own.PresentMarks);

        // 其他角色（Sales/FrontDesk）一律 403。
        await Assert.ThrowsAsync<ForbiddenActionException>(
            () => service.TeachersAsync(From, To, StaffUser(9, StaffRole.Sales)));
        await Assert.ThrowsAsync<ForbiddenActionException>(
            () => service.TeachersAsync(From, To, StaffUser(9, StaffRole.FrontDesk)));

        conn.Dispose();
    }
}
