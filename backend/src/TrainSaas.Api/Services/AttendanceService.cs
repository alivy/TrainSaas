using Microsoft.EntityFrameworkCore;
using TrainSaas.Api.Auth;
using TrainSaas.Api.Data;
using TrainSaas.Api.Domain;
using TrainSaas.Api.Dtos;
using TrainSaas.Api.Errors;

namespace TrainSaas.Api.Services;

/// <summary>
/// 点名服务（Task 13，账务心脏）。POST /api/org/sessions/{id}/attendance 的
/// 全部状态机；逐项处理、部分成功——单项失败（资格/无可用包）只影响该学员，
/// 绝不中断同批其他学员，也绝不把单项业务失败升级成请求级异常。
///
/// 守卫顺序：课次（租户过滤器，未知/跨机构 404）→ 权限（Admin 或本课次
/// 授课教师，否则 403）→ 名单（null/空 400；重复学员 400）。
///
/// 资格闸只在首点名（该课次该学员无既有 Attendance 行）时把守：在班，或
/// 名单外临时学员持有本课次产品 Active 且有余的包；已有行说明该生已被本
/// 课次接纳，改签/修正不重复校验（否则用尽课时的临时学员永远无法把
/// Present 修正为 Absent）。
///
/// 状态迁移（prev = 既有标记，或无）：
/// <list type="bullet">
/// <item>无/Leave/Absent → Present：<see cref="IDeductionService.DeductAsync"/>
/// (RollCall)；NoAvailablePackException → 该生 Ok=false "无可用课时包"且
/// 不写点名行。</item>
/// <item>Present → Present：无操作（不重复扣，幂等）。</item>
/// <item>Present → Leave/Absent：找该 (student, session) 最新一条未被冲正的
/// +1 流水，追加 Direction=-1 冲正行（Reason="点名修正"、
/// ReversesConsumptionId=原行）并回补包余量 +1；原行 append-only 一字不动；
/// 已全部冲正则只改标记。</item>
/// <item>无/Leave/Absent → Leave/Absent：只 upsert 点名行，不扣课。</item>
/// </list>
///
/// Attendance 按 (ClassSessionId, StudentId) upsert，唯一索引是数据库兜底。
/// R12 对称收敛（账务心脏，两条分支行为完全一致）：竞入的 Present 行无论
/// 经由读-改-写分支（赢家先提交，静默无异常）还是唯一索引冲突分支
/// （DbUpdateException 后清跟踪器重读、改插为改，不 500）被发现——本次目标
/// Leave/Absent 则先冲正（ReverseLatestAsync 语义）再覆写标记；本次目标
/// Present 且本次刚扣过课则本次调用是双 Present 竞态输家，按 Id 自冲正
/// 自己刚插入的 +1（确定性、append-only、恰一个竞态者自冲正）并回补包 +1，
/// 收敛结果 Ok=true。所有 +1 回补走 CAS 式 ExecuteUpdate 且与冲正行同一
/// 显式事务提交（R12 点 3）。任一点名行存在后课次幂等转 Completed。
/// </summary>
public class AttendanceService(AppDbContext db, IDeductionService deduction)
{
    /// <summary>
    /// 点名：逐项处理并返回每项结果；请求级守卫（404/403/400）先行。
    /// </summary>
    public async Task<AttendanceResult> MarkAsync(
        int classSessionId, IReadOnlyList<MarkItem>? marks, ICurrentUser caller)
    {
        // 租户过滤器内的课次：跨机构/未知 id 与不存在同态 404。
        var session = await db.ClassSessions.AsNoTracking()
            .SingleOrDefaultAsync(cs => cs.Id == classSessionId)
            ?? throw new NotFoundException("课次不存在");

        var operatorId = caller.StaffId
            ?? throw new ForbiddenActionException("员工令牌缺少 staff_id");
        if (caller.Role != StaffRole.Admin && session.TeacherId != operatorId)
        {
            throw new ForbiddenActionException("只有管理员或课次授课教师可以点名");
        }

        if (marks is null || marks.Count == 0)
        {
            throw new BusinessRuleException("点名名单不能为空");
        }

        var duplicate = marks.GroupBy(m => m.StudentId).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new BusinessRuleException($"点名名单中存在重复学员 {duplicate.Key}");
        }

        // 扣课产品取自课次模板（在班与临时学员同口径）。
        var template = await db.ClassTemplates.AsNoTracking()
            .SingleOrDefaultAsync(t => t.Id == session.TemplateId)
            ?? throw new NotFoundException("班级模板不存在");
        var productId = template.ProductId;

        var results = new List<MarkResult>(marks.Count);
        foreach (var item in marks)
        {
            // 单项异常绝不外溢成请求级失败（部分成功语义）。
            results.Add(await MarkOneAsync(session, productId, item, operatorId));
        }

        // 任一点名行存在 → 课次 Completed（幂等：守卫使重复调用 affected==0）。
        if (await db.Attendances.AsNoTracking().AnyAsync(a => a.ClassSessionId == session.Id))
        {
            await db.ClassSessions
                .Where(cs => cs.Id == session.Id && cs.Status != SessionStatus.Completed)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(cs => cs.Status, SessionStatus.Completed));
        }

        return new AttendanceResult(results);
    }

    /// <summary>
    /// 单项状态迁移（见类注释矩阵）。既有点名行用无跟踪读（DeductAsync 在
    /// CAS 失败路径会清变更跟踪器，这里不依赖跟踪状态）；写入时即时落库，
    /// 保证单项之间互不回滚。
    /// </summary>
    private async Task<MarkResult> MarkOneAsync(
        ClassSession session, int productId, MarkItem item, int operatorId)
    {
        var prev = await db.Attendances.AsNoTracking()
            .SingleOrDefaultAsync(a => a.ClassSessionId == session.Id && a.StudentId == item.StudentId);

        if (prev is null && !await IsEligibleAsync(session, productId, item.StudentId))
        {
            return new MarkResult(item.StudentId, false, "不在名单且无可用课时包");
        }

        if (item.Mark == AttendanceMark.Present)
        {
            if (prev?.Mark == AttendanceMark.Present)
            {
                // Present→Present：无操作，绝不重复扣。
                return new MarkResult(item.StudentId, true, null);
            }

            // 无/Leave/Absent→Present：先扣课；失败则该生失败且不写点名行。
            // 扣课行必须捕获（R12 点 2）：upsert 若发现竞入的 Present 行，
            // 本次调用即双 Present 竞态输家，需按 Id 自冲正这条流水。
            Consumption deducted;
            try
            {
                deducted = await deduction.DeductAsync(
                    item.StudentId, productId, session.Id, ConsumptionKind.RollCall, operatorId);
            }
            catch (NoAvailablePackException)
            {
                return new MarkResult(item.StudentId, false, "无可用课时包");
            }

            await UpsertMarkAsync(session, item, operatorId, deducted);
            return new MarkResult(item.StudentId, true, null);
        }

        // 目标 Leave/Absent：仅原 Present 需要冲正回补（其余只改标记）。
        if (prev?.Mark == AttendanceMark.Present)
        {
            await ReverseLatestAsync(session, item.StudentId, operatorId);
        }

        await UpsertMarkAsync(session, item, operatorId, deducted: null);
        return new MarkResult(item.StudentId, true, null);
    }

    /// <summary>
    /// 首点名的资格闸：在班（Enrolled），或名单外临时学员持有本课次产品
    /// Active 且有余量的课时包。跨机构/未知学员两查都落空，同态失败。
    /// </summary>
    private async Task<bool> IsEligibleAsync(ClassSession session, int productId, int studentId)
    {
        var enrolled = await db.ClassEnrollments.AsNoTracking()
            .AnyAsync(e => e.TemplateId == session.TemplateId
                && e.StudentId == studentId
                && e.Status == EnrollmentStatus.Enrolled);
        if (enrolled)
        {
            return true;
        }

        return await db.SessionPacks.AsNoTracking()
            .AnyAsync(p => p.StudentId == studentId
                && p.ProductId == productId
                && p.Status == PackStatus.Active
                && p.RemainingSessions > 0);
    }

    /// <summary>
    /// 点名行 upsert：有则改（标记 + 操作人），无则插；并发赢家抢先插入时
    /// 唯一索引 (ClassSessionId,StudentId) 把 DbUpdateException 兜成
    /// "重读后改插为改"（收敛，不 500）。
    ///
    /// R12 对称收敛（两条分支行为完全一致）：无论竞入的 Present 行经由
    /// 读-改-写分支（赢家先提交，静默无异常）还是唯一索引冲突分支（插入
    /// 撞车）被发现，写入标记前都先走 <see cref="ConvergeRacedPresentAsync"/>
    /// 的同一套账务调和——绝不允许"行收敛了、账没收敛"的静默漏账。
    /// </summary>
    /// <param name="deducted">
    /// 本次调用刚插入的 +1 扣课行（仅目标 Present 且扣课成功时非 null）；
    /// 竞态输家自冲正按此 Id 确定性定位，无需 LIFO 查找。
    /// </param>
    private async Task UpsertMarkAsync(
        ClassSession session, MarkItem item, int operatorId, Consumption? deducted)
    {
        var row = await db.Attendances
            .SingleOrDefaultAsync(a => a.ClassSessionId == session.Id && a.StudentId == item.StudentId);
        if (row is not null)
        {
            // 读-改-写分支：行可能是并发赢家在本调用 prev 读取之后竞入的。
            await ConvergeRacedPresentAsync(session, item, operatorId, deducted, row.Mark);
            row.Mark = item.Mark;
            row.OperatorId = operatorId;
            await db.SaveChangesAsync();
            return;
        }

        db.Attendances.Add(new Attendance
        {
            ClassSessionId = session.Id,
            StudentId = item.StudentId,
            Mark = item.Mark,
            OperatorId = operatorId,
            CreatedAt = DateTime.Now,
            // InstitutionId 留 0：SaveChanges 按当前员工令牌自动填充。
        });
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // 并发赢家抢先提交了同一 (ClassSessionId,StudentId)（事务已回滚）：
            // 丢弃冲突实体重读该行，改插为改。
            db.ChangeTracker.Clear();
            var raced = await db.Attendances
                .SingleOrDefaultAsync(a => a.ClassSessionId == session.Id && a.StudentId == item.StudentId);
            if (raced is null)
            {
                // 不是唯一索引冲突：原样上抛，绝不静默丢点名。
                throw;
            }

            // 冲突分支：与读-改-写分支走同一套调和（对称，R12 点 1/2）。
            await ConvergeRacedPresentAsync(session, item, operatorId, deducted, raced.Mark);
            raced.Mark = item.Mark;
            raced.OperatorId = operatorId;
            await db.SaveChangesAsync();
        }
    }

    /// <summary>
    /// 竞入 Present 行的账务调和（R12 裁定；upsert 两条分支共用，行为一致）。
    /// 既有行已为 Present 时：
    /// <list type="bullet">
    /// <item>本次目标 Present 且本次刚扣过课（deducted 非 null；Present→Present
    /// 在状态机中提前返回、绝不走到 upsert，故此形态唯一）：本次调用是双
    /// Present 竞态输家 → <see cref="SelfReverseAsync"/> 按 Id 自冲正自己的
    /// 扣课行并回补包 +1。确定性（自己的行 Id，无 LIFO 查找）、append-only、
    /// 恰一个竞态者自冲正；收敛结果 Ok=true（行已 Present，终态正确）。</item>
    /// <item>本次目标 Leave/Absent：与状态迁移矩阵一致，先
    /// <see cref="ReverseLatestAsync"/> 冲正最新未冲正的 +1（竞入赢家的扣课）
    /// 再覆写标记——绝不盲写导致赢家的扣课悬挂在 Leave/Absent 行上。
    /// ReverseLatestAsync 幂等（无未冲正流水即无操作），常规
    /// Present→Leave/Absent 路径在状态机中已冲正过时此处自然不再重复。</item>
    /// </list>
    /// 行非 Present（或目标 Present 且本次未扣课）：无账务可调，直接返回。
    /// </summary>
    private async Task ConvergeRacedPresentAsync(
        ClassSession session, MarkItem item, int operatorId, Consumption? deducted,
        AttendanceMark racedMark)
    {
        if (racedMark != AttendanceMark.Present)
        {
            return;
        }

        if (item.Mark == AttendanceMark.Present)
        {
            if (deducted is not null)
            {
                await SelfReverseAsync(deducted, operatorId);
            }
            return;
        }

        await ReverseLatestAsync(session, item.StudentId, operatorId);
    }

    /// <summary>
    /// Present→Leave/Absent 的冲正：找该 (student, session) 最新一条未被冲正
    /// 的 +1 流水（RollCall；WalkIn 无课次关联，天然不在候选内；已冲正的
    /// 原行被 NOT EXISTS 排除），追加 Direction=-1 冲正行；原行 append-only
    /// 一字不动。无未冲正流水（从未扣过/已修正过）则什么都不做（幂等）。
    /// 包回补 +1 走 CAS 式 ExecuteUpdate，与冲正行同一显式事务提交
    /// （R12 点 3，见 <see cref="RestorePackSessionAsync"/>）。
    /// </summary>
    private async Task ReverseLatestAsync(ClassSession session, int studentId, int operatorId)
    {
        var original = await db.Consumptions
            .Where(c => c.StudentId == studentId
                && c.ClassSessionId == session.Id
                && c.Direction == 1
                && !db.Consumptions.Any(r => r.ReversesConsumptionId == c.Id))
            .OrderByDescending(c => c.Id)
            .FirstOrDefaultAsync();
        if (original is null)
        {
            // 已被冲正（或从未扣过）：只改点名标记。
            return;
        }

        // 包回补与冲正行同一显式事务：要么都提交，要么都不提交。
        await using var tx = await db.Database.BeginTransactionAsync();
        await RestorePackSessionAsync(original.SessionPackId);
        db.Consumptions.Add(new Consumption
        {
            StudentId = studentId,
            SessionPackId = original.SessionPackId,
            ClassSessionId = session.Id,
            Kind = ConsumptionKind.Reversal,
            Direction = -1,
            Reason = "点名修正",
            ReversesConsumptionId = original.Id,
            OperatorId = operatorId,
            CreatedAt = DateTime.Now,
            // InstitutionId 留 0：SaveChanges 按当前员工令牌自动填充。
        });
        await db.SaveChangesAsync();
        await tx.CommitAsync();
    }

    /// <summary>
    /// 双 Present 竞态输家的自冲正（R12 点 2）：按 Id 确定性冲正本次调用
    /// 自己刚插入的 +1 扣课行（无 LIFO 查找，绝不冲正赢家的流水），追加
    /// Direction=-1、Kind=Reversal、Reason="点名修正"、ReversesConsumptionId=
    /// 自己行 的新流水并回补包 +1，同一显式事务提交；原行 append-only
    /// 一字不动。恰一个竞态者自冲正（upsert 发现既有 Present 行的那一个）。
    /// </summary>
    private async Task SelfReverseAsync(Consumption own, int operatorId)
    {
        await using var tx = await db.Database.BeginTransactionAsync();
        await RestorePackSessionAsync(own.SessionPackId);
        db.Consumptions.Add(new Consumption
        {
            StudentId = own.StudentId,
            SessionPackId = own.SessionPackId,
            ClassSessionId = own.ClassSessionId,
            Kind = ConsumptionKind.Reversal,
            Direction = -1,
            Reason = "点名修正",
            ReversesConsumptionId = own.Id,
            OperatorId = operatorId,
            CreatedAt = DateTime.Now,
            // InstitutionId 留 0：SaveChanges 按当前员工令牌自动填充。
        });
        await db.SaveChangesAsync();
        await tx.CommitAsync();
    }

    /// <summary>
    /// 包余量 +1 回补（R12 点 3，绑定）：CAS 式 ExecuteUpdate 直接对数据库
    /// 求值（RemainingSessions = RemainingSessions + 1，WHERE Id==packId），
    /// 由调用方在同一显式事务内与冲正行一起提交——绝不跟踪读-改-写，
    /// 消除"并发扣减 vs 回补"用陈旧快照互相覆盖账务的窗口。
    /// </summary>
    private async Task RestorePackSessionAsync(int packId) =>
        await db.SessionPacks
            .Where(p => p.Id == packId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(p => p.RemainingSessions, p => p.RemainingSessions + 1));
}
