using Microsoft.EntityFrameworkCore;
using TrainSaas.Api.Auth;
using TrainSaas.Api.Data;
using TrainSaas.Api.Domain;
using TrainSaas.Api.Dtos;
using TrainSaas.Api.Errors;

namespace TrainSaas.Api.Services;

/// <summary>
/// 到店核销、流水列表与撤销冲正（Task 14）。账本 append-only：撤销只追加
/// Direction=-1 的镜像行，原行一字不动。
///
/// 核销守卫顺序：学员（租户过滤器内，未知/跨机构 404）→ 包（404）→
/// 包属该学员（400 "课时包不属于该学员"）→ Active（400 "课时包已作废"）→
/// 有余量（400 "课时不足"）；实际扣减按包的 ProductId 走
/// <see cref="IDeductionService.DeductAsync"/>（同产品最早先扣、CAS 防负），
/// classSessionId 恒为 null——点名修正按 (student, session) LIFO 冲正，
/// 带课次 id 的 WalkIn 行会被误冲正（Task 13 账本协同裁定）。
///
/// 冲正（R13 加固）：目标须 Direction=+1（400）；reason 必填、trim、≤200
/// （400）；已存在 ReversesConsumptionId==目标 的行 → 409；权限 Admin 任意，
/// 或本人操作且当天（CreatedAt.Date==DateTime.Today，跨天 403）。包余量 +1
/// 走 CAS 式 ExecuteUpdate（作废包也恢复计数、状态不变），与镜像行同一显式
/// 事务提交；ReversesConsumptionId 唯一索引是并发双撤销的数据库兜底——
/// 输家的 DbUpdateException 被捕获、回滚后重查，确系已撤销收敛为 409
/// （余量只回补一次），其它数据库失败原样上抛，绝不静默吞掉。
///
/// 列表角色范围（[StaffOnly] 之下服务层裁决）：Admin/FrontDesk 全机构；
/// Sales 仅 AssignedSalesId==自己 的学员的行；Teacher 仅 OperatorId==自己
/// 或行对应课次 TeacherId==自己。日期窗 from 含当日零点、to 排他次日零点；
/// 排序 CreatedAt desc, Id desc；名称批量解析（无 N+1）。
/// </summary>
public class ConsumptionService(AppDbContext db, IDeductionService deduction)
{
    /// <summary>撤销原因长度上限（trim 后）。</summary>
    private const int MaxReasonLength = 200;

    /// <summary>
    /// POST /api/org/consumptions/walk-in：到店核销。校验选中的包后按其
    /// ProductId 走标准扣减（同产品最早先扣），返回新流水 id 与核销后该包
    /// 余量。流水行 Kind=WalkIn、ClassSessionId=null、操作人=调用者。
    /// </summary>
    public async Task<WalkInResult> WalkInAsync(int studentId, int sessionPackId, ICurrentUser caller)
    {
        var operatorId = RequireStaffId(caller);

        // 学员与包都在租户过滤器内查：未知/跨机构一律 404，不泄露存在性。
        _ = await db.Students.AsNoTracking().SingleOrDefaultAsync(s => s.Id == studentId)
            ?? throw new NotFoundException("学员不存在");
        var pack = await db.SessionPacks.AsNoTracking()
            .SingleOrDefaultAsync(p => p.Id == sessionPackId)
            ?? throw new NotFoundException("课时包不存在");

        if (pack.StudentId != studentId)
        {
            throw new BusinessRuleException("课时包不属于该学员");
        }

        if (pack.Status != PackStatus.Active)
        {
            throw new BusinessRuleException("课时包已作废");
        }

        if (pack.RemainingSessions <= 0)
        {
            throw new BusinessRuleException("课时不足");
        }

        // 扣减复用 Task 13 账本心脏：同产品最早先扣、CAS 防负、同事务落
        // +1 流水。WalkIn 行必须不带课次 id（见类注释）。
        var consumption = await deduction.DeductAsync(
            studentId, pack.ProductId, classSessionId: null, ConsumptionKind.WalkIn, operatorId);

        // 核销后该包余量（DeductAsync 扣的可能是同产品更早的包）。
        var remaining = await db.SessionPacks.AsNoTracking()
            .Where(p => p.Id == sessionPackId)
            .Select(p => p.RemainingSessions)
            .SingleAsync();
        return new WalkInResult(consumption.Id, remaining);
    }

    /// <summary>
    /// GET /api/org/consumptions 的数据源：角色范围内按 studentId/日期窗/kind
    /// 过滤，CreatedAt desc, Id desc 分页；名称批量解析（无 N+1）。
    /// </summary>
    public async Task<Paged<ConsumptionItem>> ListAsync(
        int? studentId, DateTime? from, DateTime? to, ConsumptionKind? kind,
        int page, int pageSize, ICurrentUser caller)
    {
        var query = ScopeConsumptionsFor(caller).AsNoTracking();

        if (studentId is not null)
        {
            query = query.Where(c => c.StudentId == studentId.Value);
        }

        if (from is not null)
        {
            // from 含当日零点（inclusive）。
            var fromDate = from.Value.Date;
            query = query.Where(c => c.CreatedAt >= fromDate);
        }

        if (to is not null)
        {
            // to 排他次日零点（inclusive 到当日末尾）。
            var toExclusive = to.Value.Date.AddDays(1);
            query = query.Where(c => c.CreatedAt < toExclusive);
        }

        if (kind is not null)
        {
            query = query.Where(c => c.Kind == kind.Value);
        }

        var (pageNumber, pageSizeValue) = Paging.Normalize(page, pageSize);
        var total = await query.CountAsync();
        var rows = await query
            .OrderByDescending(c => c.CreatedAt)
            .ThenByDescending(c => c.Id)
            .Skip((pageNumber - 1) * pageSizeValue)
            .Take(pageSizeValue)
            .ToListAsync();

        // 名称/课次时间批量解析（无 N+1）：本页行一次取齐再内存拼装。
        var studentIds = rows.Select(c => c.StudentId).Distinct().ToList();
        var studentNames = studentIds.Count == 0
            ? new Dictionary<int, string>()
            : await db.Students.AsNoTracking()
                .Where(s => studentIds.Contains(s.Id))
                .ToDictionaryAsync(s => s.Id, s => s.Name);

        var packIds = rows.Select(c => c.SessionPackId).Distinct().ToList();
        var packProductIds = packIds.Count == 0
            ? new Dictionary<int, int>()
            : (await db.SessionPacks.AsNoTracking()
                .Where(p => packIds.Contains(p.Id))
                .Select(p => new { p.Id, p.ProductId })
                .ToListAsync())
                .ToDictionary(x => x.Id, x => x.ProductId);
        var productIds = packProductIds.Values.Distinct().ToList();
        var productNames = productIds.Count == 0
            ? new Dictionary<int, string>()
            : await db.Products.AsNoTracking()
                .Where(p => productIds.Contains(p.Id))
                .ToDictionaryAsync(p => p.Id, p => p.Name);

        var operatorIds = rows.Select(c => c.OperatorId).Distinct().ToList();
        var operatorNames = operatorIds.Count == 0
            ? new Dictionary<int, string>()
            : await db.Staff.AsNoTracking()
                .Where(s => operatorIds.Contains(s.Id))
                .ToDictionaryAsync(s => s.Id, s => s.Name);

        var sessionIds = rows
            .Where(c => c.ClassSessionId is not null)
            .Select(c => c.ClassSessionId!.Value)
            .Distinct()
            .ToList();
        var sessionStarts = sessionIds.Count == 0
            ? new Dictionary<int, DateTime>()
            : await db.ClassSessions.AsNoTracking()
                .Where(cs => sessionIds.Contains(cs.Id))
                .ToDictionaryAsync(cs => cs.Id, cs => cs.StartAt);

        return new Paged<ConsumptionItem>(total, rows.Select(c => new ConsumptionItem(
            c.Id,
            c.StudentId,
            studentNames.GetValueOrDefault(c.StudentId),
            packProductIds.TryGetValue(c.SessionPackId, out var productId)
                ? productNames.GetValueOrDefault(productId)
                : null,
            c.Kind.ToString(),
            c.Direction,
            c.Reason,
            operatorNames.GetValueOrDefault(c.OperatorId),
            c.ClassSessionId is int sessionId ? sessionStarts.GetValueOrDefault(sessionId) : null,
            c.CreatedAt)).ToList());
    }

    /// <summary>
    /// POST /api/org/consumptions/{id}/reverse：撤销冲正（append-only）。
    /// 守卫顺序：目标（租户过滤器内 404）→ 只能撤销扣减流水（400）→
    /// reason（400）→ 已撤销（409）→ 权限（403）。包余量 +1 与镜像行同一
    /// 显式事务；唯一索引兜底并发双撤销（见类注释）。
    /// </summary>
    public async Task<Consumption> ReverseAsync(int consumptionId, string? reason, ICurrentUser caller)
    {
        // 目标行只读（append-only：原行永不 UPDATE），无跟踪即可。
        var target = await db.Consumptions.AsNoTracking()
            .SingleOrDefaultAsync(c => c.Id == consumptionId)
            ?? throw new NotFoundException("流水不存在");

        if (target.Direction != 1)
        {
            throw new BusinessRuleException("只能撤销扣减流水");
        }

        var trimmedReason = ValidateReason(reason);

        if (await db.Consumptions.AsNoTracking()
                .AnyAsync(r => r.ReversesConsumptionId == consumptionId))
        {
            throw new ConflictException("该流水已撤销");
        }

        var operatorId = RequireStaffId(caller);
        if (caller.Role != StaffRole.Admin
            && (target.OperatorId != operatorId || target.CreatedAt.Date != DateTime.Today))
        {
            throw new ForbiddenActionException("只有管理员或当日操作人本人可以撤销");
        }

        // 包回补与镜像行同一显式事务：要么都提交，要么都不提交。回补走
        // CAS 式 ExecuteUpdate 直接对数据库求值（与点名修正同一模式），
        // 不加状态守卫——作废包也恢复计数、状态不变。
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.SessionPacks
            .Where(p => p.Id == target.SessionPackId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(p => p.RemainingSessions, p => p.RemainingSessions + 1));

        var mirror = new Consumption
        {
            StudentId = target.StudentId,
            SessionPackId = target.SessionPackId,
            ClassSessionId = target.ClassSessionId,
            Kind = ConsumptionKind.Reversal,
            Direction = -1,
            Reason = trimmedReason,
            ReversesConsumptionId = target.Id,
            OperatorId = operatorId,
            CreatedAt = DateTime.Now,
            // InstitutionId 留 0：SaveChanges 按当前员工令牌自动填充。
        };
        db.Consumptions.Add(mirror);
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // 并发双撤销：赢家先提交了冲正行，唯一索引 (ReversesConsumptionId)
            // 把输家的插入变成 DbUpdateException。回滚（包回补一并撤销）后
            // 重查确认：确系已撤销 → 409 收敛（余量只回补一次）；不是 →
            // 原样上抛，绝不把其它数据库失败静默转成 409。
            await tx.RollbackAsync();
            db.ChangeTracker.Clear();
            if (await db.Consumptions.AsNoTracking()
                    .AnyAsync(r => r.ReversesConsumptionId == consumptionId))
            {
                throw new ConflictException("该流水已撤销");
            }

            throw;
        }

        await tx.CommitAsync();
        return mirror;
    }

    /// <summary>
    /// 角色范围（列表）：Admin/FrontDesk 全机构（租户过滤器已限本机构）；
    /// Sales 仅名下学员的行；Teacher 仅本人操作或本人授课课次的行。
    /// </summary>
    private IQueryable<Consumption> ScopeConsumptionsFor(ICurrentUser caller) => caller.Role switch
    {
        StaffRole.Admin or StaffRole.FrontDesk => db.Consumptions,
        StaffRole.Sales => ScopedBySales(caller),
        StaffRole.Teacher => ScopedByTeacher(caller),
        _ => throw new ForbiddenActionException("当前角色无权访问课时流水"),
    };

    private IQueryable<Consumption> ScopedBySales(ICurrentUser caller)
    {
        var salesId = RequireStaffId(caller);
        return db.Consumptions.Where(c => db.Students
            .Where(s => s.Id == c.StudentId && s.AssignedSalesId == salesId)
            .Any());
    }

    private IQueryable<Consumption> ScopedByTeacher(ICurrentUser caller)
    {
        var teacherId = RequireStaffId(caller);
        return db.Consumptions.Where(c => c.OperatorId == teacherId
            || db.ClassSessions
                .Where(cs => cs.Id == c.ClassSessionId && cs.TeacherId == teacherId)
                .Any());
    }

    /// <summary>撤销原因：必填、trim 后非空且 ≤200 字。</summary>
    private static string ValidateReason(string? reason)
    {
        var trimmed = reason?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            throw new BusinessRuleException("撤销原因不能为空");
        }

        if (trimmed.Length > MaxReasonLength)
        {
            throw new BusinessRuleException($"撤销原因不能超过 {MaxReasonLength} 个字符");
        }

        return trimmed;
    }

    /// <summary>员工令牌必须携带 staff_id，否则视为损坏的令牌（403）。</summary>
    private static int RequireStaffId(ICurrentUser caller) =>
        caller.StaffId ?? throw new ForbiddenActionException("员工令牌缺少 staff_id");
}
