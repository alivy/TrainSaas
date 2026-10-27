using Microsoft.EntityFrameworkCore;
using TrainSaas.Api.Auth;
using TrainSaas.Api.Data;
using TrainSaas.Api.Domain;
using TrainSaas.Api.Dtos;
using TrainSaas.Api.Errors;

namespace TrainSaas.Api.Services;

/// <summary>
/// 家长端只读服务（Task 15）。家长令牌跨机构（ParentAccount 按手机号全局唯一，
/// 无 institution_id claim），租户过滤器会把一切 ITenanted 查询折叠成空集，
/// 因此这里是**设计内的第二个租户过滤豁免**（第一个是公开介绍页
/// <see cref="IntentService"/>），纪律与审查口径如下：
///
/// <list type="bullet">
/// <item>每一条查询都以 <c>IgnoreQueryFilters()</c> 开头，并**紧跟**显式归属
/// 谓词：要么是令牌手机号（<c>Phone == caller.Phone</c>），要么是已验证归属的
/// 孩子的 StudentId + InstitutionId。没有任何一条查询可以只 IgnoreQueryFilters
/// 而不带显式谓词——那等于把全库开放给任意家长。</item>
/// <item>归属校验先行：packs/records/schedule 三个孩子级路由都先经
/// <see cref="RequireOwnedChildAsync"/>，它把"存在性 + 手机号归属 + IsActive"
/// 压进同一条查询，未命中一律 <see cref="NotFoundException"/>（404）——绝不
/// 403，避免用状态码差异泄露别人孩子的存在性。</item>
/// <item>后续查询同时带 StudentId 与 InstitutionId 双谓词：学员 id 全局唯一，
/// InstitutionId 是纵深防御（孩子的机构之外的行一律不可见）。</item>
/// <item>读路径全部 AsNoTracking；唯一的写路径是课表的惰性生成（R15）——
/// 经 <see cref="SessionGenerationService.EnsureSessionsForInstitutionAsync"/>
/// 按"已验证归属的孩子所属机构"物化未来实例，机构 id 取自孩子行而非请求参数。</item>
/// <item>名称（机构/产品/教师/订单号/课次时间）一律批量解析后在内存拼装，
/// 无 N+1；金额不参与任何聚合（余量/课时数是 int，直接读列）。</item>
/// </list>
/// </summary>
public class ParentService(AppDbContext db, SessionGenerationService sessionGeneration)
{
    /// <summary>
    /// GET /api/parent/children：令牌手机号名下的全部在读孩子（跨机构），
    /// 按 institutionId、studentId 升序。机构名/编码批量解析。
    /// </summary>
    public async Task<List<ParentChildItem>> ListChildrenAsync(ICurrentUser caller)
    {
        var phone = RequirePhone(caller);

        // 豁免纪律：IgnoreQueryFilters + 显式 Phone == 令牌手机号（且在读）。
        var children = await db.Students
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(s => s.Phone == phone && s.IsActive)
            .OrderBy(s => s.InstitutionId)
            .ThenBy(s => s.Id)
            .Select(s => new { s.Id, s.Name, s.InstitutionId })
            .ToListAsync();
        if (children.Count == 0)
        {
            return [];
        }

        // Institution 非 ITenanted（本就无过滤器），仍统一 IgnoreQueryFilters +
        // 显式 id 集合谓词以保持纪律。
        var institutionIds = children.Select(c => c.InstitutionId).Distinct().ToList();
        var institutions = await db.Institutions
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(i => institutionIds.Contains(i.Id))
            .ToDictionaryAsync(i => i.Id, i => new { i.Name, i.Code });

        return children
            .Select(c =>
            {
                var org = institutions.GetValueOrDefault(c.InstitutionId);
                return new ParentChildItem(c.Id, c.Name, c.InstitutionId, org?.Name, org?.Code);
            })
            .ToList();
    }

    /// <summary>
    /// GET /api/parent/packs：某个孩子的课时包（含已作废的历史包），按 packId
    /// 降序（新买的在前）。productName/orderNo 批量解析；purchasedAt 取课时包
    /// 创建时刻（= 订单确认时刻）。
    /// </summary>
    public async Task<List<ParentPackItem>> ListPacksAsync(int studentId, ICurrentUser caller)
    {
        var child = await RequireOwnedChildAsync(studentId, caller);

        var packs = await db.SessionPacks
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(p => p.StudentId == child.Id && p.InstitutionId == child.InstitutionId)
            .OrderByDescending(p => p.Id)
            .ToListAsync();
        if (packs.Count == 0)
        {
            return [];
        }

        var productIds = packs.Select(p => p.ProductId).Distinct().ToList();
        var productNames = await db.Products
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(p => p.InstitutionId == child.InstitutionId && productIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.Name);

        var orderIds = packs.Select(p => p.OrderId).Distinct().ToList();
        var orderNos = await db.Orders
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(o => o.InstitutionId == child.InstitutionId && orderIds.Contains(o.Id))
            .ToDictionaryAsync(o => o.Id, o => o.OrderNo);

        return packs
            .Select(p => new ParentPackItem(
                p.Id,
                productNames.GetValueOrDefault(p.ProductId),
                p.TotalSessions,
                p.RemainingSessions,
                p.Status.ToString(),
                orderNos.GetValueOrDefault(p.OrderId),
                p.CreatedAt))
            .ToList();
    }

    /// <summary>
    /// GET /api/parent/records：某个孩子的课时流水（含 Direction=-1 的冲正行），
    /// CreatedAt desc, Id desc 分页；from 含当日零点、to 排他次日零点（与员工端
    /// 流水列表同语义）。productName 经课时包 → 产品，classSessionStartAt 经
    /// 课次批量解析。
    /// </summary>
    public async Task<Paged<ParentRecordItem>> ListRecordsAsync(
        int studentId, DateTime? from, DateTime? to, int page, int pageSize, ICurrentUser caller)
    {
        var child = await RequireOwnedChildAsync(studentId, caller);

        var query = db.Consumptions
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(c => c.StudentId == child.Id && c.InstitutionId == child.InstitutionId);

        if (from is not null)
        {
            var fromDate = from.Value.Date;
            query = query.Where(c => c.CreatedAt >= fromDate);
        }

        if (to is not null)
        {
            var toExclusive = to.Value.Date.AddDays(1);
            query = query.Where(c => c.CreatedAt < toExclusive);
        }

        var (pageNumber, pageSizeValue) = Paging.Normalize(page, pageSize);
        // total 是 int 计数，SQL CountAsync 安全（金额才必须内存聚合）。
        var total = await query.CountAsync();
        var rows = await query
            .OrderByDescending(c => c.CreatedAt)
            .ThenByDescending(c => c.Id)
            .Skip((pageNumber - 1) * pageSizeValue)
            .Take(pageSizeValue)
            .ToListAsync();
        if (rows.Count == 0)
        {
            return new Paged<ParentRecordItem>(total, []);
        }

        // 流水 → 课时包 → 产品名（两级批量解析，无 N+1）。
        var packIds = rows.Select(c => c.SessionPackId).Distinct().ToList();
        var packProductIds = await db.SessionPacks
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(p => p.InstitutionId == child.InstitutionId && packIds.Contains(p.Id))
            .Select(p => new { p.Id, p.ProductId })
            .ToListAsync();
        var productIds = packProductIds.Select(x => x.ProductId).Distinct().ToList();
        var productNames = productIds.Count == 0
            ? new Dictionary<int, string>()
            : await db.Products
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(p => p.InstitutionId == child.InstitutionId && productIds.Contains(p.Id))
                .ToDictionaryAsync(p => p.Id, p => p.Name);
        var productIdByPack = packProductIds.ToDictionary(x => x.Id, x => x.ProductId);

        // 课次时间（RollCall 行有课次；WalkIn 行恒为 null）。
        var sessionIds = rows
            .Where(c => c.ClassSessionId is not null)
            .Select(c => c.ClassSessionId!.Value)
            .Distinct()
            .ToList();
        var sessionStarts = sessionIds.Count == 0
            ? new Dictionary<int, DateTime>()
            : await db.ClassSessions
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(cs => cs.InstitutionId == child.InstitutionId && sessionIds.Contains(cs.Id))
                .ToDictionaryAsync(cs => cs.Id, cs => cs.StartAt);

        return new Paged<ParentRecordItem>(total, rows.Select(c => new ParentRecordItem(
            c.Id,
            c.Kind.ToString(),
            c.Direction,
            productIdByPack.TryGetValue(c.SessionPackId, out var productId)
                ? productNames.GetValueOrDefault(productId)
                : null,
            c.CreatedAt,
            c.Reason,
            c.ClassSessionId is int sessionId ? sessionStarts.GetValueOrDefault(sessionId) : null)).ToList());
    }

    /// <summary>
    /// GET /api/parent/schedule：某个孩子未来 days 天的课表——在班（Enrolled）
    /// 报名 + 启用（IsActive）模板的 Scheduled 实例，
    /// <c>StartAt ∈ [Now, Now.AddDays(days)]</c>，按 startAt 升序。room 读时取自
    /// 模板（既有约定：ClassSession 不存教室）。
    ///
    /// R15：查询前先按"孩子所属机构"触发一次惰性生成——课次是惰性物化的，若机构侧
    /// 近期没人查过课表，库里可能一节实例都没有，家长会看到空课表（违背"家长端可看
    /// 固定型课表"）。生成范围与机构侧一致（该机构全部启用模板），窗口与本次查询
    /// 同口径；孩子跨机构时每次调用各按自己的机构触发。生成窗口仍受 28 天钳制
    /// （SessionGenerationService），故 days&gt;28 时只有前 28 天会被物化，其余部分
    /// 依赖机构侧已生成的实例。
    /// </summary>
    public async Task<List<ParentScheduleItem>> ListScheduleAsync(int studentId, int days, ICurrentUser caller)
    {
        var child = await RequireOwnedChildAsync(studentId, caller);
        var now = DateTime.Now;
        var until = now.AddDays(days);

        var enrolledTemplateIds = await db.ClassEnrollments
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(e => e.StudentId == child.Id
                && e.InstitutionId == child.InstitutionId
                && e.Status == EnrollmentStatus.Enrolled)
            .Select(e => e.TemplateId)
            .ToListAsync();
        if (enrolledTemplateIds.Count == 0)
        {
            return [];
        }

        // 模板本身也要显式限定机构 + 启用；读到的模板稍后同时提供 room/productId。
        var templates = await db.ClassTemplates
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(t => t.InstitutionId == child.InstitutionId
                && t.IsActive
                && enrolledTemplateIds.Contains(t.Id))
            .ToListAsync();
        if (templates.Count == 0)
        {
            return [];
        }

        // R15：家长端唯一的写路径。机构 id 取自已验证归属的孩子行（绝不取自请求
        // 参数），生成方法内部同样 IgnoreQueryFilters + 显式机构谓词、新行显式落
        // InstitutionId（家长令牌无租户上下文，自动填充会抛 403）。
        await sessionGeneration.EnsureSessionsForInstitutionAsync(child.InstitutionId, now, until);

        var templateIds = templates.Select(t => t.Id).ToList();
        var sessions = await db.ClassSessions
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(cs => cs.InstitutionId == child.InstitutionId
                && cs.Status == SessionStatus.Scheduled
                && cs.StartAt >= now
                && cs.StartAt <= until
                && templateIds.Contains(cs.TemplateId))
            .OrderBy(cs => cs.StartAt)
            .ToListAsync();
        if (sessions.Count == 0)
        {
            return [];
        }

        var templateById = templates.ToDictionary(t => t.Id);
        var productIds = templates.Select(t => t.ProductId).Distinct().ToList();
        var productNames = await db.Products
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(p => p.InstitutionId == child.InstitutionId && productIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.Name);
        var teacherIds = sessions.Select(cs => cs.TeacherId).Distinct().ToList();
        var teacherNames = await db.Staff
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(s => s.InstitutionId == child.InstitutionId && teacherIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => s.Name);
        var institutionName = await db.Institutions
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(i => i.Id == child.InstitutionId)
            .Select(i => i.Name)
            .SingleOrDefaultAsync();

        return sessions.Select(cs =>
        {
            var template = templateById.GetValueOrDefault(cs.TemplateId);
            return new ParentScheduleItem(
                cs.StartAt,
                cs.EndAt,
                template is null ? null : productNames.GetValueOrDefault(template.ProductId),
                teacherNames.GetValueOrDefault(cs.TeacherId),
                template?.Room,
                institutionName);
        }).ToList();
    }

    /// <summary>
    /// 孩子归属校验（豁免纪律的核心）：存在性、令牌手机号归属、在读三件事压在
    /// 同一条查询里，未命中一律 404 —— 别人的孩子与不存在的 id 响应同态，
    /// 绝不 403（不泄露存在性）。
    /// </summary>
    private async Task<Student> RequireOwnedChildAsync(int studentId, ICurrentUser caller)
    {
        var phone = RequirePhone(caller);
        return await db.Students
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleOrDefaultAsync(s => s.Id == studentId && s.Phone == phone && s.IsActive)
            ?? throw new NotFoundException("学员不存在");
    }

    /// <summary>家长令牌必须携带 phone，否则视为损坏的令牌（403）。</summary>
    private static string RequirePhone(ICurrentUser caller) =>
        caller.Phone ?? throw new ForbiddenActionException("家长令牌缺少 phone");
}
