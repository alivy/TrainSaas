using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using TrainSaas.Api.Auth;
using TrainSaas.Api.Data;
using TrainSaas.Api.Domain;
using TrainSaas.Api.Errors;
using TrainSaas.Api.Services;

namespace TrainSaas.Tests.Unit;

/// <summary>
/// ConsumptionService 单元测试（TestDb 内存 SQLite + FakeCurrentUser）——
/// 到店核销、撤销冲正与流水列表的账务语义：
/// <list type="bullet">
/// <item>核销：包属本学员、Active、有余量，扣的就是所选包（R14 修正：
/// DeductFromPackAsync 同款 CAS，不滑到同产品最早的包；WalkIn 行必须
/// ClassSessionId=null——点名修正按课次 LIFO 冲正，带课次的 WalkIn 行
/// 会被误冲正）；不属于该学员/已作废/课时不足分别 400，未知/跨机构
/// 学员与包一律 404。</item>
/// <item>冲正：append-only——原行一字不动，追加 Direction=-1、Kind=Reversal、
/// ReversesConsumptionId=原行 的镜像行，包余量 +1 与镜像行同一显式事务；
/// 作废包也恢复计数、状态不变；权限先于 409（R14：无权者一律 403，
/// 不泄露冲正状态）；双重撤销 409；reason 必填、trim、≤200；
/// 权限 Admin 任意，或本人操作且当天（跨天 403，Review Focus 5）。</item>
/// <item>列表角色范围：Admin/FrontDesk 全机构；Sales 仅名下学员；Teacher 仅
/// 本人操作或本人授课课次的行。日期窗 from 含当日零点、to 排他次日零点。</item>
/// <item>唯一索引兜底（Task 11/12 同款）：ReversesConsumptionId 唯一，
/// 同一原行的第二条冲正行 DbUpdateException；NULL 互异、扣减行不受影响。</item>
/// </list>
/// </summary>
public class ConsumptionServiceTests
{
    /// <summary>机构 1 的管理员（StaffId=1）。</summary>
    private readonly FakeCurrentUser _admin = StaffUser(1, StaffRole.Admin, "管理员");

    private static FakeCurrentUser StaffUser(int staffId, StaffRole role, string name) => new()
    {
        UserType = UserType.Staff,
        InstitutionId = 1,
        StaffId = staffId,
        Role = role,
        Phone = $"139{staffId:D8}",
        Name = name,
    };

    private static (AppDbContext Db, ConsumptionService Service, SqliteConnection Conn) Create(
        ICurrentUser user)
    {
        var (db, conn) = TestDb.Create(user);
        return (db, new ConsumptionService(db, new DeductionService(db)), conn);
    }

    private static async Task<Student> SeedStudentAsync(
        AppDbContext db, string name, string phone, int? assignedSalesId = null, int institutionId = 1)
    {
        var student = new Student
        {
            InstitutionId = institutionId,
            Name = name,
            Phone = phone,
            AssignedSalesId = assignedSalesId,
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

    private static async Task<Staff> SeedStaffAsync(
        AppDbContext db, string name, string phone, StaffRole role)
    {
        var staff = new Staff
        {
            InstitutionId = 1,
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

    private static async Task<ClassSession> SeedSessionAsync(
        AppDbContext db, int teacherId, DateTime startAt)
    {
        var session = new ClassSession
        {
            InstitutionId = 1,
            TemplateId = 900,
            TeacherId = teacherId,
            StartAt = startAt,
            EndAt = startAt.AddMinutes(90),
            Status = SessionStatus.Scheduled,
        };
        db.ClassSessions.Add(session);
        await db.SaveChangesAsync();
        return session;
    }

    private static async Task<Consumption> SeedConsumptionAsync(
        AppDbContext db,
        int studentId,
        int packId,
        int operatorId,
        DateTime? createdAt = null,
        ConsumptionKind kind = ConsumptionKind.WalkIn,
        int direction = 1,
        int? classSessionId = null,
        string? reason = null,
        int? reversesConsumptionId = null)
    {
        var consumption = new Consumption
        {
            InstitutionId = 1,
            StudentId = studentId,
            SessionPackId = packId,
            ClassSessionId = classSessionId,
            Kind = kind,
            Direction = direction,
            Reason = reason,
            ReversesConsumptionId = reversesConsumptionId,
            OperatorId = operatorId,
            CreatedAt = createdAt ?? DateTime.Now,
        };
        db.Consumptions.Add(consumption);
        await db.SaveChangesAsync();
        return consumption;
    }

    private static async Task<int> RemainingAsync(AppDbContext db, int packId) =>
        await db.SessionPacks.AsNoTracking()
            .Where(p => p.Id == packId)
            .Select(p => p.RemainingSessions)
            .SingleAsync();

    // ---------- 到店核销 ----------

    [Fact]
    public async Task Walkin_deducts_selected_pack_and_records_operator()
    {
        var frontDesk = StaffUser(3, StaffRole.FrontDesk, "前台甲");
        var (db, service, conn) = Create(frontDesk);
        var student = await SeedStudentAsync(db, "学员甲", "13811113001");
        var product = await SeedProductAsync(db, "少儿课时包");
        var pack = await SeedPackAsync(db, student.Id, product.Id, 901, 5);

        var result = await service.WalkInAsync(student.Id, pack.Id, frontDesk);

        // 响应：新流水 id + 核销后该包余量。
        Assert.Equal(4, result.RemainingSessions);
        Assert.Equal(4, await RemainingAsync(db, pack.Id));

        var row = await db.Consumptions.AsNoTracking().SingleAsync(c => c.Id == result.Id);
        Assert.Equal(student.Id, row.StudentId);
        Assert.Equal(pack.Id, row.SessionPackId);
        // WalkIn 行必须不带课次 id（Task 13 账本协同裁定）：点名修正按
        // (student, session) LIFO 冲正，带课次的 WalkIn 行会被误冲正。
        Assert.Null(row.ClassSessionId);
        Assert.Equal(ConsumptionKind.WalkIn, row.Kind);
        Assert.Equal(1, row.Direction);
        Assert.Equal(3, row.OperatorId);
        Assert.Null(row.Reason);
        Assert.Null(row.ReversesConsumptionId);
        Assert.Equal(1, row.InstitutionId);
        // 恰落一行流水。
        Assert.Single(await db.Consumptions.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// R14 修正：核销扣的就是所选包——即便同产品存在更早购买的可用包，
    /// 也绝不滑到最早优先（DeductFromPackAsync 对指定包 CAS）；响应
    /// remainingSessions 即所选包扣后真值。
    /// </summary>
    [Fact]
    public async Task Walkin_deducts_selected_newer_pack_not_earliest()
    {
        var frontDesk = StaffUser(3, StaffRole.FrontDesk, "前台甲");
        var (db, service, conn) = Create(frontDesk);
        var student = await SeedStudentAsync(db, "学员寅", "13811113020");
        var product = await SeedProductAsync(db, "少儿课时包");
        var early = await SeedPackAsync(db, student.Id, product.Id, 920, 5,
            createdAt: new DateTime(2026, 10, 1, 10, 0, 0));
        var later = await SeedPackAsync(db, student.Id, product.Id, 921, 3,
            createdAt: new DateTime(2026, 10, 10, 10, 0, 0));

        var result = await service.WalkInAsync(student.Id, later.Id, frontDesk);

        // 扣的是所选的较新包：较新包 -1、较早包纹丝不动。
        Assert.Equal(2, await RemainingAsync(db, later.Id));
        Assert.Equal(5, await RemainingAsync(db, early.Id));
        var row = await db.Consumptions.AsNoTracking().SingleAsync(c => c.Id == result.Id);
        Assert.Equal(later.Id, row.SessionPackId);
        Assert.Equal(student.Id, row.StudentId);
        Assert.Equal(ConsumptionKind.WalkIn, row.Kind);
        Assert.Null(row.ClassSessionId);
        // 响应余量即所选包扣后真值。
        Assert.Equal(2, result.RemainingSessions);
        Assert.Single(await db.Consumptions.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Walkin_on_zero_remaining_pack_returns_error()
    {
        var frontDesk = StaffUser(3, StaffRole.FrontDesk, "前台甲");
        var (db, service, conn) = Create(frontDesk);
        var student = await SeedStudentAsync(db, "学员乙", "13811113002");
        var product = await SeedProductAsync(db, "少儿课时包");
        var pack = await SeedPackAsync(db, student.Id, product.Id, 902, 0);

        // Review Focus 3：余量为 0 的 Active 包 → 400 "课时不足"。
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(
            () => service.WalkInAsync(student.Id, pack.Id, frontDesk));
        Assert.Equal("课时不足", ex.Message);

        // 失败不落流水、不动包。
        Assert.Empty(await db.Consumptions.IgnoreQueryFilters().ToListAsync());
        Assert.Equal(0, await RemainingAsync(db, pack.Id));
    }

    [Fact]
    public async Task Walkin_on_voided_pack_returns_error()
    {
        var frontDesk = StaffUser(3, StaffRole.FrontDesk, "前台甲");
        var (db, service, conn) = Create(frontDesk);
        var student = await SeedStudentAsync(db, "学员丙", "13811113003");
        var product = await SeedProductAsync(db, "少儿课时包");
        var voided = await SeedPackAsync(db, student.Id, product.Id, 903, 5, PackStatus.Voided);

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(
            () => service.WalkInAsync(student.Id, voided.Id, frontDesk));
        Assert.Equal("课时包已作废", ex.Message);

        // 状态校验先于余量：作废且余量为 0 → 仍是 "课时包已作废"。
        var voidedEmpty = await SeedPackAsync(db, student.Id, product.Id, 904, 0, PackStatus.Voided);
        var ex2 = await Assert.ThrowsAsync<BusinessRuleException>(
            () => service.WalkInAsync(student.Id, voidedEmpty.Id, frontDesk));
        Assert.Equal("课时包已作废", ex2.Message);

        Assert.Empty(await db.Consumptions.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task Walkin_on_other_students_pack_returns_error()
    {
        var frontDesk = StaffUser(3, StaffRole.FrontDesk, "前台甲");
        var (db, service, conn) = Create(frontDesk);
        var studentA = await SeedStudentAsync(db, "学员A", "13811113004");
        var studentB = await SeedStudentAsync(db, "学员B", "13811113005");
        var product = await SeedProductAsync(db, "少儿课时包");
        var packB = await SeedPackAsync(db, studentB.Id, product.Id, 905, 5);

        // 包不属于该学员 → 400。
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(
            () => service.WalkInAsync(studentA.Id, packB.Id, frontDesk));
        Assert.Equal("课时包不属于该学员", ex.Message);

        // 未知包 / 未知学员 → 404。
        var noPack = await Assert.ThrowsAsync<NotFoundException>(
            () => service.WalkInAsync(studentA.Id, 123456, frontDesk));
        Assert.Equal("课时包不存在", noPack.Message);
        var noStudent = await Assert.ThrowsAsync<NotFoundException>(
            () => service.WalkInAsync(654321, packB.Id, frontDesk));
        Assert.Equal("学员不存在", noStudent.Message);

        // 跨机构学员与包经租户过滤器不可见：与不存在同态 404，不泄露存在性。
        var awayStudent = await SeedStudentAsync(db, "外校学员", "13811113006", institutionId: 2);
        var awayPack = await SeedPackAsync(db, awayStudent.Id, product.Id, 906, 5, institutionId: 2);
        await Assert.ThrowsAsync<NotFoundException>(
            () => service.WalkInAsync(awayStudent.Id, awayPack.Id, frontDesk));

        Assert.Empty(await db.Consumptions.IgnoreQueryFilters().ToListAsync());
    }

    // ---------- 撤销冲正 ----------

    [Fact]
    public async Task Reverse_restores_remaining_and_writes_mirror_row()
    {
        var (db, service, conn) = Create(_admin);
        var student = await SeedStudentAsync(db, "学员丁", "13811113007");
        var product = await SeedProductAsync(db, "少儿课时包");
        var pack = await SeedPackAsync(db, student.Id, product.Id, 907, 3);
        var session = await SeedSessionAsync(db, teacherId: 42, new DateTime(2026, 10, 20, 18, 0, 0));
        var original = await SeedConsumptionAsync(
            db, student.Id, pack.Id, operatorId: 55,
            createdAt: DateTime.Today.AddDays(-1).AddHours(10),
            kind: ConsumptionKind.RollCall, classSessionId: session.Id, reason: "点名");

        var mirror = await service.ReverseAsync(original.Id, "  误操作，予以冲正  ", _admin);

        // 包余量 +1。
        Assert.Equal(4, await RemainingAsync(db, pack.Id));

        // 镜像行字段齐备：Direction=-1、Kind=Reversal、reason 已 trim、
        // 指向原行，学员/包/课次照抄原行，操作人是撤销者（非原操作人）。
        Assert.Equal(-1, mirror.Direction);
        Assert.Equal(ConsumptionKind.Reversal, mirror.Kind);
        Assert.Equal("误操作，予以冲正", mirror.Reason);
        Assert.Equal(original.Id, mirror.ReversesConsumptionId);
        Assert.Equal(original.StudentId, mirror.StudentId);
        Assert.Equal(original.SessionPackId, mirror.SessionPackId);
        Assert.Equal(original.ClassSessionId, mirror.ClassSessionId);
        Assert.Equal(1, mirror.OperatorId);
        Assert.Equal(1, mirror.InstitutionId);

        // 原行 append-only 一字不动；账上恰两行。
        var target = await db.Consumptions.AsNoTracking().SingleAsync(c => c.Id == original.Id);
        Assert.Equal(1, target.Direction);
        Assert.Equal(ConsumptionKind.RollCall, target.Kind);
        Assert.Equal("点名", target.Reason);
        Assert.Equal(55, target.OperatorId);
        Assert.Null(target.ReversesConsumptionId);
        Assert.Equal(2, await db.Consumptions.AsNoTracking().CountAsync());

        // 作废包也恢复计数、状态不变（裁定点）。
        var voided = await SeedPackAsync(db, student.Id, product.Id, 908, 0, PackStatus.Voided);
        var original2 = await SeedConsumptionAsync(
            db, student.Id, voided.Id, operatorId: 55,
            createdAt: DateTime.Today.AddDays(-1).AddHours(11),
            kind: ConsumptionKind.RollCall);
        await service.ReverseAsync(original2.Id, "作废包冲正", _admin);
        Assert.Equal(1, await RemainingAsync(db, voided.Id));
        Assert.Equal(PackStatus.Voided, await db.SessionPacks.AsNoTracking()
            .Where(p => p.Id == voided.Id).Select(p => p.Status).SingleAsync());
    }

    [Fact]
    public async Task Double_reverse_returns_409()
    {
        var (db, service, conn) = Create(_admin);
        var student = await SeedStudentAsync(db, "学员戊", "13811113008");
        var product = await SeedProductAsync(db, "少儿课时包");
        var pack = await SeedPackAsync(db, student.Id, product.Id, 909, 3);
        var original = await SeedConsumptionAsync(db, student.Id, pack.Id, operatorId: 1);

        var mirror = await service.ReverseAsync(original.Id, "第一次撤销", _admin);

        // Review Focus 2：已存在冲正行 → 409，余量只恢复一次。
        var ex = await Assert.ThrowsAsync<ConflictException>(
            () => service.ReverseAsync(original.Id, "第二次撤销", _admin));
        Assert.Equal("该流水已撤销", ex.Message);
        Assert.Equal(4, await RemainingAsync(db, pack.Id));
        Assert.Single(await db.Consumptions.AsNoTracking()
            .Where(c => c.ReversesConsumptionId == original.Id).ToListAsync());

        // R14 守卫顺序：权限先于 409——非本人非 Admin 对已冲正流水一律
        // 403，不泄露该流水的冲正状态。
        var outsider = StaffUser(6, StaffRole.Sales, "销售乙");
        await Assert.ThrowsAsync<ForbiddenActionException>(
            () => service.ReverseAsync(original.Id, "越权探测", outsider));

        // 未知/跨机构 id → 404，不泄露存在性。
        var notFound = await Assert.ThrowsAsync<NotFoundException>(
            () => service.ReverseAsync(987654, "撤销不存在的流水", _admin));
        Assert.Equal("流水不存在", notFound.Message);

        // 冲正行本身（Direction=-1）不可再撤销 → 400。
        var ex2 = await Assert.ThrowsAsync<BusinessRuleException>(
            () => service.ReverseAsync(mirror.Id, "撤销镜像行", _admin));
        Assert.Equal("只能撤销扣减流水", ex2.Message);
    }

    [Fact]
    public async Task Operator_can_reverse_own_record_same_day_but_not_yesterday()
    {
        // Review Focus 5：日界用 CreatedAt=yesterday 的既有行钉死。
        var operatorUser = StaffUser(5, StaffRole.FrontDesk, "前台乙");
        var (db, service, conn) = Create(operatorUser);
        var student = await SeedStudentAsync(db, "学员己", "13811113009");
        var product = await SeedProductAsync(db, "少儿课时包");
        var pack = await SeedPackAsync(db, student.Id, product.Id, 910, 2);
        var today = await SeedConsumptionAsync(
            db, student.Id, pack.Id, operatorId: 5,
            createdAt: DateTime.Today.AddHours(9), kind: ConsumptionKind.WalkIn);
        var yesterday = await SeedConsumptionAsync(
            db, student.Id, pack.Id, operatorId: 5,
            createdAt: DateTime.Today.AddDays(-1).AddHours(21), kind: ConsumptionKind.WalkIn);

        // 跨天：本人也不再能撤销（403），不落镜像、不动余量。
        await Assert.ThrowsAsync<ForbiddenActionException>(
            () => service.ReverseAsync(yesterday.Id, "昨天的录错了", operatorUser));
        Assert.Equal(2, await RemainingAsync(db, pack.Id));
        Assert.DoesNotContain(
            await db.Consumptions.AsNoTracking().ToListAsync(),
            c => c.ReversesConsumptionId == yesterday.Id);

        // 当天：本人可撤销。
        var mirror = await service.ReverseAsync(today.Id, "今天的录错了", operatorUser);
        Assert.Equal(5, mirror.OperatorId);
        Assert.Equal(today.Id, mirror.ReversesConsumptionId);
        Assert.Equal(3, await RemainingAsync(db, pack.Id));

        // 非 Admin 的他人（即便当天）不能撤销别人的行。
        var anotherToday = await SeedConsumptionAsync(
            db, student.Id, pack.Id, operatorId: 5,
            createdAt: DateTime.Today.AddHours(10), kind: ConsumptionKind.WalkIn);
        var other = StaffUser(6, StaffRole.Sales, "销售甲");
        await Assert.ThrowsAsync<ForbiddenActionException>(
            () => service.ReverseAsync(anotherToday.Id, "越权撤销", other));
    }

    [Fact]
    public async Task Admin_can_reverse_any_record()
    {
        var (db, service, conn) = Create(_admin);
        var student = await SeedStudentAsync(db, "学员庚", "13811113010");
        var product = await SeedProductAsync(db, "少儿课时包");
        var pack = await SeedPackAsync(db, student.Id, product.Id, 911, 3);
        // 他人操作、昨天的行：Admin 不受日界与本人限制。
        var original = await SeedConsumptionAsync(
            db, student.Id, pack.Id, operatorId: 5,
            createdAt: DateTime.Today.AddDays(-1).AddHours(20), kind: ConsumptionKind.WalkIn);

        var mirror = await service.ReverseAsync(original.Id, "管理员代撤销", _admin);

        Assert.Equal(original.Id, mirror.ReversesConsumptionId);
        Assert.Equal(1, mirror.OperatorId);
        Assert.Equal(-1, mirror.Direction);
        Assert.Equal(4, await RemainingAsync(db, pack.Id));
    }

    [Fact]
    public async Task Reverse_without_reason_returns_400()
    {
        var (db, service, conn) = Create(_admin);
        var student = await SeedStudentAsync(db, "学员辛", "13811113011");
        var product = await SeedProductAsync(db, "少儿课时包");
        var pack = await SeedPackAsync(db, student.Id, product.Id, 912, 3);
        var original = await SeedConsumptionAsync(db, student.Id, pack.Id, operatorId: 1);

        // 缺失/空白 → 400。
        foreach (var bad in new string?[] { null, string.Empty, "   ", "\t" })
        {
            await Assert.ThrowsAsync<BusinessRuleException>(
                () => service.ReverseAsync(original.Id, bad, _admin));
        }

        // 超过 200 字 → 400。
        await Assert.ThrowsAsync<BusinessRuleException>(
            () => service.ReverseAsync(original.Id, new string('长', 201), _admin));

        // 被拒的撤销不落镜像、不动余量。
        Assert.Single(await db.Consumptions.AsNoTracking().ToListAsync());
        Assert.Equal(3, await RemainingAsync(db, pack.Id));

        // 恰好 200 字合法（trim 后长度边界）。
        var mirror = await service.ReverseAsync(original.Id, new string('长', 200), _admin);
        Assert.Equal(200, mirror.Reason!.Length);
    }

    // ---------- 流水列表角色范围与过滤 ----------

    [Fact]
    public async Task Sales_list_only_contains_own_assigned_students_rows()
    {
        var (db, service, conn) = Create(_admin);
        var mine = await SeedStudentAsync(db, "我的学员", "13811113012", assignedSalesId: 7);
        var theirs = await SeedStudentAsync(db, "他人学员", "13811113013", assignedSalesId: 8);
        var loose = await SeedStudentAsync(db, "未归属学员", "13811113014");
        var product = await SeedProductAsync(db, "少儿课时包");
        var packMine = await SeedPackAsync(db, mine.Id, product.Id, 913, 5);
        var packTheirs = await SeedPackAsync(db, theirs.Id, product.Id, 914, 5);
        var packLoose = await SeedPackAsync(db, loose.Id, product.Id, 915, 5);
        var rowMine = await SeedConsumptionAsync(db, mine.Id, packMine.Id, operatorId: 1);
        await SeedConsumptionAsync(db, theirs.Id, packTheirs.Id, operatorId: 1);
        await SeedConsumptionAsync(db, loose.Id, packLoose.Id, operatorId: 1);

        // Sales 只见名下学员（AssignedSalesId==自己）的行；未归属学员不可见。
        var sales = StaffUser(7, StaffRole.Sales, "销售甲");
        var page = await service.ListAsync(null, null, null, null, 1, 20, sales);
        Assert.Equal(1, page.Total);
        var item = Assert.Single(page.Items);
        Assert.Equal(rowMine.Id, item.Id);
        Assert.Equal("我的学员", item.StudentName);

        // Admin / FrontDesk 见全机构。
        Assert.Equal(3, (await service.ListAsync(null, null, null, null, 1, 20, _admin)).Total);
        Assert.Equal(3, (await service.ListAsync(
            null, null, null, null, 1, 20, StaffUser(4, StaffRole.FrontDesk, "前台丙"))).Total);

        // 范围优先于过滤：Sales 按他人学员过滤 → 空，不泄露越权数据。
        Assert.Equal(0, (await service.ListAsync(theirs.Id, null, null, null, 1, 20, sales)).Total);
    }

    [Fact]
    public async Task Teacher_list_only_contains_own_operated_or_taught_rows()
    {
        var (db, service, conn) = Create(_admin);
        var student = await SeedStudentAsync(db, "学员壬", "13811113015");
        var product = await SeedProductAsync(db, "少儿课时包");
        var pack = await SeedPackAsync(db, student.Id, product.Id, 916, 9);
        var mySession = await SeedSessionAsync(db, teacherId: 9, new DateTime(2026, 10, 19, 18, 0, 0));
        var theirSession = await SeedSessionAsync(db, teacherId: 10, new DateTime(2026, 10, 19, 20, 0, 0));

        // Teacher 可见：本人操作的行（无课次）+ 本人授课课次的行（他人操作）。
        var operated = await SeedConsumptionAsync(db, student.Id, pack.Id, operatorId: 9);
        var taught = await SeedConsumptionAsync(
            db, student.Id, pack.Id, operatorId: 77, classSessionId: mySession.Id);
        // 不可见：他人授课课次的行、他人操作且无课次的行。
        await SeedConsumptionAsync(
            db, student.Id, pack.Id, operatorId: 77, classSessionId: theirSession.Id);
        await SeedConsumptionAsync(db, student.Id, pack.Id, operatorId: 77);

        var teacher = StaffUser(9, StaffRole.Teacher, "教师九");
        var page = await service.ListAsync(null, null, null, null, 1, 20, teacher);
        Assert.Equal(2, page.Total);
        Assert.Equal(
            [operated.Id, taught.Id],
            page.Items.Select(i => i.Id).OrderBy(id => id));
        // 课次行带出开课时间；无课次行为 null。
        Assert.Equal(mySession.StartAt, page.Items.Single(i => i.Id == taught.Id).ClassSessionStartAt);
        Assert.Null(page.Items.Single(i => i.Id == operated.Id).ClassSessionStartAt);
    }

    [Fact]
    public async Task List_applies_date_window_kind_and_student_filters()
    {
        var (db, service, conn) = Create(_admin);
        var operatorStaff = await SeedStaffAsync(db, "操作员甲", "13911113016", StaffRole.FrontDesk);
        var student = await SeedStudentAsync(db, "学员癸", "13811113017");
        var other = await SeedStudentAsync(db, "学员子", "13811113018");
        var product = await SeedProductAsync(db, "少儿课时包");
        var pack = await SeedPackAsync(db, student.Id, product.Id, 917, 20);
        var packOther = await SeedPackAsync(db, other.Id, product.Id, 918, 20);
        var session = await SeedSessionAsync(db, teacherId: 42, new DateTime(2026, 10, 21, 18, 0, 0));

        var before = await SeedConsumptionAsync(db, student.Id, pack.Id, operatorStaff.Id,
            createdAt: new DateTime(2026, 10, 20, 9, 0, 0), kind: ConsumptionKind.RollCall);
        var fromEdge = await SeedConsumptionAsync(db, student.Id, pack.Id, operatorStaff.Id,
            createdAt: new DateTime(2026, 10, 21, 0, 0, 0), kind: ConsumptionKind.WalkIn);
        var inWindow = await SeedConsumptionAsync(db, student.Id, pack.Id, operatorStaff.Id,
            createdAt: new DateTime(2026, 10, 21, 15, 0, 0), kind: ConsumptionKind.RollCall,
            classSessionId: session.Id, reason: "点名");
        var otherStudent = await SeedConsumptionAsync(db, other.Id, packOther.Id, operatorStaff.Id,
            createdAt: new DateTime(2026, 10, 21, 12, 0, 0), kind: ConsumptionKind.WalkIn);
        var late = await SeedConsumptionAsync(db, student.Id, pack.Id, operatorStaff.Id,
            createdAt: new DateTime(2026, 10, 22, 23, 59, 0), kind: ConsumptionKind.WalkIn);
        var toEdge = await SeedConsumptionAsync(db, student.Id, pack.Id, operatorStaff.Id,
            createdAt: new DateTime(2026, 10, 23, 0, 0, 0), kind: ConsumptionKind.RollCall);

        // 日期窗（钉死语义）：from 含当日零点起、to 含当日到次日零点（排他）。
        var page = await service.ListAsync(
            null, new DateTime(2026, 10, 21), new DateTime(2026, 10, 22), null, 1, 20, _admin);
        Assert.Equal(4, page.Total);
        // CreatedAt 降序：late(22日23:59) → inWindow(21日15:00) →
        // otherStudent(21日12:00) → fromEdge(21日00:00)；窗外两行不出现。
        Assert.Equal(
            [late.Id, inWindow.Id, otherStudent.Id, fromEdge.Id],
            page.Items.Select(i => i.Id));
        Assert.DoesNotContain(page.Items, i => i.Id == before.Id || i.Id == toEdge.Id);

        // kind 过滤。
        var walkIns = await service.ListAsync(
            null, new DateTime(2026, 10, 21), new DateTime(2026, 10, 22),
            ConsumptionKind.WalkIn, 1, 20, _admin);
        Assert.Equal([late.Id, otherStudent.Id, fromEdge.Id], walkIns.Items.Select(i => i.Id));

        // studentId 过滤。
        var onlyOther = await service.ListAsync(other.Id, null, null, null, 1, 20, _admin);
        Assert.Equal(otherStudent.Id, Assert.Single(onlyOther.Items).Id);

        // 列表项字段：名称批量解析（学员/产品/操作员），kind 为枚举名。
        var item = page.Items.Single(i => i.Id == inWindow.Id);
        Assert.Equal(student.Id, item.StudentId);
        Assert.Equal("学员癸", item.StudentName);
        Assert.Equal("少儿课时包", item.ProductName);
        Assert.Equal("操作员甲", item.OperatorName);
        Assert.Equal("RollCall", item.Kind);
        Assert.Equal(1, item.Direction);
        Assert.Equal("点名", item.Reason);
        Assert.Equal(session.StartAt, item.ClassSessionStartAt);
        Assert.Equal(inWindow.CreatedAt, item.CreatedAt);
        Assert.Null(page.Items.Single(i => i.Id == late.Id).ClassSessionStartAt);

        // 同 CreatedAt 按 Id 降序；分页取第二页 total 不变。
        var tieA = await SeedConsumptionAsync(db, student.Id, pack.Id, operatorStaff.Id,
            createdAt: new DateTime(2026, 10, 25, 8, 0, 0));
        var tieB = await SeedConsumptionAsync(db, student.Id, pack.Id, operatorStaff.Id,
            createdAt: new DateTime(2026, 10, 25, 8, 0, 0));
        var tiePage = await service.ListAsync(
            null, new DateTime(2026, 10, 25), new DateTime(2026, 10, 25), null, 1, 1, _admin);
        Assert.Equal(2, tiePage.Total);
        Assert.Equal(tieB.Id, Assert.Single(tiePage.Items).Id);
        var secondPage = await service.ListAsync(
            null, new DateTime(2026, 10, 25), new DateTime(2026, 10, 25), null, 2, 1, _admin);
        Assert.Equal(tieA.Id, Assert.Single(secondPage.Items).Id);
    }

    // ---------- 数据库兜底 ----------

    /// <summary>
    /// 数据库兜底（Task 11/12 索引测试同款）：即使绕过服务直插两条指向同一
    /// 原行的冲正行，ReversesConsumptionId 唯一索引也拒绝第二条
    /// （DbUpdateException）——并发双撤销的输家由 ReverseAsync 捕获重查后
    /// 收敛为 409，而非静默双回补课时。SQLite 把 NULL 视为互异：多条
    /// ReversesConsumptionId=null 的扣减行不受该索引影响。
    /// </summary>
    [Fact]
    public async Task Consumption_reverses_unique_index_rejects_second_reversal_row()
    {
        var (db, conn) = TestDb.Create(_admin);
        var student = await SeedStudentAsync(db, "学员丑", "13811113019");
        var product = await SeedProductAsync(db, "少儿课时包");
        var pack = await SeedPackAsync(db, student.Id, product.Id, 919, 5);
        var original = await SeedConsumptionAsync(db, student.Id, pack.Id, operatorId: 1);

        // NULL 互异：再插一条 ReversesConsumptionId=null 的扣减行不受影响。
        await SeedConsumptionAsync(db, student.Id, pack.Id, operatorId: 1);

        // 第一条冲正行成功。
        await SeedConsumptionAsync(db, student.Id, pack.Id, operatorId: 1,
            kind: ConsumptionKind.Reversal, direction: -1,
            reason: "撤销", reversesConsumptionId: original.Id);

        // 指向同一原行的第二条冲正行被唯一索引拒绝。
        db.Consumptions.Add(new Consumption
        {
            InstitutionId = 1,
            StudentId = student.Id,
            SessionPackId = pack.Id,
            Kind = ConsumptionKind.Reversal,
            Direction = -1,
            Reason = "并发撤销",
            ReversesConsumptionId = original.Id,
            OperatorId = 2,
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    // ---------- 服务级并发冲正收敛（T14①，随 I4 补齐） ----------

    /// <summary>
    /// 竞态重放拦截器（测试缝，一次性）：挂在"输家"上下文的**事务开始之前**
    /// （TransactionStarting 在 BEGIN 执行前触发；此刻输家已完成"是否已撤销"
    /// 预检、锁定了陈旧视图，且延迟事务尚未持任何锁），让"赢家"在独立连接上
    /// 完整跑完 ReverseAsync 并提交——确定性重现并发双撤销：输家随后的镜像行
    /// 插入必撞 ReversesConsumptionId 唯一索引。钩子绝不能挂在输家事务**内**
    /// 的写命令上：那时输家已持写锁，赢家的写会 database-is-locked 死锁。
    /// HookFired 自证测试确实走到了交错窗口。
    /// </summary>
    private sealed class WinnerRunsBeforeLoserTransaction(Func<Task> winnerWrites)
        : DbTransactionInterceptor
    {
        private Func<Task>? _winnerWrites = winnerWrites;

        public bool HookFired { get; private set; }

        public override InterceptionResult<DbTransaction> TransactionStarting(
            DbConnection connection, TransactionStartingEventData eventData,
            InterceptionResult<DbTransaction> result)
        {
            MaybeRace().GetAwaiter().GetResult();
            return base.TransactionStarting(connection, eventData, result);
        }

        public override async ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(
            DbConnection connection, TransactionStartingEventData eventData,
            InterceptionResult<DbTransaction> result, CancellationToken cancellationToken = default)
        {
            await MaybeRace();
            return await base.TransactionStartingAsync(connection, eventData, result, cancellationToken);
        }

        private async Task MaybeRace()
        {
            var hook = _winnerWrites;
            if (hook is null)
            {
                return;
            }

            _winnerWrites = null;
            HookFired = true;
            await hook();
        }
    }

    /// <summary>临时文件库上下文（双上下文竞态测试）：同一文件、独立连接，可选挂拦截器。</summary>
    private static AppDbContext CreateFileDb(
        string dataSource, ICurrentUser user, IInterceptor? interceptor = null)
    {
        var builder = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(dataSource);
        if (interceptor is not null)
        {
            builder.AddInterceptors(interceptor);
        }

        return new AppDbContext(builder.Options, user);
    }

    private static void TryDeleteDbFiles(string dbPath)
    {
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
        {
            try
            {
                File.Delete(dbPath + suffix);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    /// T14①（账本延后项，随 I4 补齐）：服务级并发双撤销的收敛路径此前零
    /// 覆盖（移除 ReverseAsync 的 catch 段全套仍绿——索引测试只证数据库层
    /// 拒绝）。确定性交错（临时文件库 + 双上下文 + 钩子挂在输家的第一条
    /// UPDATE，即包回补 ExecuteUpdate 执行之前）：双方都通过"已存在冲正行"
    /// 预检 → 赢家先提交镜像行 → 输家的插入撞 ReversesConsumptionId 唯一
    /// 索引 → DbUpdateException 必须收敛为 409（回滚后复查确认），余量只
    /// 回补一次、镜像行恰一条、原行 append-only 一字不动。
    /// </summary>
    [Fact]
    public async Task Concurrent_double_reverse_converges_to_single_409_and_single_restore()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"trainsaas-race-rev-{Guid.NewGuid():N}.db");
        var dataSource = $"DataSource={dbPath}";
        try
        {
            var adminA = StaffUser(1, StaffRole.Admin, "管理员甲");
            var adminB = StaffUser(2, StaffRole.Admin, "管理员乙");
            Student student;
            SessionPack pack;
            Consumption original;
            using (var seed = CreateFileDb(dataSource, adminA))
            {
                seed.Database.EnsureCreated();
                student = await SeedStudentAsync(seed, "并发学员", "13811113021");
                var product = await SeedProductAsync(seed, "少儿课时包");
                pack = await SeedPackAsync(seed, student.Id, product.Id, 921, 3);
                original = await SeedConsumptionAsync(seed, student.Id, pack.Id, operatorId: 1);
            }

            using var winnerDb = CreateFileDb(dataSource, adminA);
            var winner = new ConsumptionService(winnerDb, new DeductionService(winnerDb));

            // 钩子挂输家：其冲正事务 BEGIN 之前（预检已完成、未持任何锁），
            // 赢家已完整提交同一原行的镜像行。
            Consumption? winnerMirror = null;
            var interceptor = new WinnerRunsBeforeLoserTransaction(async () =>
                winnerMirror = await winner.ReverseAsync(original.Id, "赢家撤销", adminA));
            using var loserDb = CreateFileDb(dataSource, adminB, interceptor);
            var loser = new ConsumptionService(loserDb, new DeductionService(loserDb));

            // 输家收敛为 409（唯一索引 DbUpdateException 被捕获、回滚、复查），
            // 而不是 500，更不是静默双回补。
            var ex = await Assert.ThrowsAsync<ConflictException>(
                () => loser.ReverseAsync(original.Id, "输家撤销", adminB));
            Assert.Equal("该流水已撤销", ex.Message);
            Assert.True(interceptor.HookFired, "竞态钩子未触发：测试没有走到交错窗口");
            Assert.NotNull(winnerMirror);

            // 镜像行恰一条（赢家的），余量恰好 +1 一次：3 → 4。
            var mirrors = await loserDb.Consumptions.AsNoTracking()
                .Where(c => c.ReversesConsumptionId == original.Id).ToListAsync();
            var mirror = Assert.Single(mirrors);
            Assert.Equal(winnerMirror!.Id, mirror.Id);
            Assert.Equal("赢家撤销", mirror.Reason);
            Assert.Equal(-1, mirror.Direction);
            Assert.Equal(4, await RemainingAsync(loserDb, pack.Id));

            // 原行 append-only：一字不动；账上恰两行。
            var untouched = await loserDb.Consumptions.AsNoTracking()
                .SingleAsync(c => c.Id == original.Id);
            Assert.Equal(1, untouched.Direction);
            Assert.Null(untouched.Reason);
            Assert.Equal(2, await loserDb.Consumptions.AsNoTracking().CountAsync());
        }
        finally
        {
            TryDeleteDbFiles(dbPath);
        }
    }
}
