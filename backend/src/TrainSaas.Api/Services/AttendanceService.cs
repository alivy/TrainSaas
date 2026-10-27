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
/// Attendance 按 (ClassSessionId, StudentId) upsert，唯一索引是数据库兜底：
/// 并发插入冲突（DbUpdateException）清跟踪器后重读，按 upsert 语义改插为
/// 改（对方若刚标为 Present 且本次要改成 Leave/Absent，先走冲正使账务
/// 自洽），不产生 500。任一点名行存在后课次幂等转 Completed。
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
            try
            {
                await deduction.DeductAsync(
                    item.StudentId, productId, session.Id, ConsumptionKind.RollCall, operatorId);
            }
            catch (NoAvailablePackException)
            {
                return new MarkResult(item.StudentId, false, "无可用课时包");
            }

            await UpsertMarkAsync(session, item, operatorId);
            return new MarkResult(item.StudentId, true, null);
        }

        // 目标 Leave/Absent：仅原 Present 需要冲正回补（其余只改标记）。
        if (prev?.Mark == AttendanceMark.Present)
        {
            await ReverseLatestAsync(session, item.StudentId, operatorId);
        }

        await UpsertMarkAsync(session, item, operatorId);
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
    /// </summary>
    private async Task UpsertMarkAsync(ClassSession session, MarkItem item, int operatorId)
    {
        var row = await db.Attendances
            .SingleOrDefaultAsync(a => a.ClassSessionId == session.Id && a.StudentId == item.StudentId);
        if (row is not null)
        {
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

            // 对手刚把该生标为 Present（已扣课）而本次要改成 Leave/Absent：
            // 按矩阵先冲正再改标记，账务保持自洽。
            if (raced.Mark == AttendanceMark.Present && item.Mark != AttendanceMark.Present)
            {
                await ReverseLatestAsync(session, item.StudentId, operatorId);
            }

            raced.Mark = item.Mark;
            raced.OperatorId = operatorId;
            await db.SaveChangesAsync();
        }
    }

    /// <summary>
    /// Present→Leave/Absent 的冲正：找该 (student, session) 最新一条未被冲正
    /// 的 +1 流水（RollCall；WalkIn 无课次关联，天然不在候选内；已冲正的
    /// 原行被 NOT EXISTS 排除），追加 Direction=-1 冲正行并回补包余量 +1
    /// （跟踪直改——+1 永不违反约束）；原行 append-only 一字不动。无未冲正
    /// 流水（从未扣过/已修正过）则什么都不做（幂等）。
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

        // 包回补与冲正行同一次 SaveChanges 提交。
        var pack = await db.SessionPacks.SingleOrDefaultAsync(p => p.Id == original.SessionPackId);
        if (pack is not null)
        {
            pack.RemainingSessions += 1;
        }

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
    }
}
