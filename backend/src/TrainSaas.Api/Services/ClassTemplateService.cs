using System.Globalization;
using Microsoft.EntityFrameworkCore;
using TrainSaas.Api.Data;
using TrainSaas.Api.Domain;
using TrainSaas.Api.Dtos;
using TrainSaas.Api.Errors;

namespace TrainSaas.Api.Services;

/// <summary>
/// 班级模板管理服务（AdminOnly 入口）。业务规则：
/// <list type="bullet">
/// <item>产品必须存在（租户过滤）且 Type==Fixed，否则 400
/// "仅固定排课型产品可建班级"；</item>
/// <item>teacherId 必须是本机构在职且 Role∈{Teacher,Admin} 的员工，否则 400
/// （跨机构 id 被租户过滤器隐藏，同样 400）；</item>
/// <item>startTime/endTime 为严格 "HH:mm"；endTime 必须晚于 startTime；
/// dayOfWeek 为 0-6（0=周日）；capacity 若填 ≥1；</item>
/// <item>新建模板显式激活（实体默认 false）；修改模板只影响之后新生成的
/// 课次，已生成课次不变（SessionGenerationService 不回填历史）。</item>
/// </list>
/// 所有业务规则违反一律抛 <see cref="BusinessRuleException"/>（400），
/// 未知/跨机构模板 id 抛 <see cref="NotFoundException"/>（404）。
/// </summary>
public class ClassTemplateService(AppDbContext db)
{
    private const string TimeFormat = "HH:mm";

    /// <summary>
    /// GET /api/org/templates?productId 的数据源：本机构（租户过滤器）内按
    /// Id 升序分页；productId 可选。列表项含读时解析的 productName/
    /// teacherName 与在班（Enrolled）学员数 enrolledCount（Task 12；退班的
    /// 历史行不计入）。
    /// </summary>
    public async Task<Paged<TemplateListItem>> ListAsync(int? productId, int? page, int? pageSize)
    {
        var query = db.ClassTemplates.AsNoTracking();
        if (productId is not null)
        {
            query = query.Where(t => t.ProductId == productId);
        }

        var (pageNumber, pageSizeValue) = Paging.Normalize(page, pageSize);
        var total = await query.CountAsync();
        var templates = await query
            .OrderBy(t => t.Id)
            .Skip((pageNumber - 1) * pageSizeValue)
            .Take(pageSizeValue)
            .ToListAsync();

        var items = new List<TemplateListItem>();
        if (templates.Count > 0)
        {
            var productIds = templates.Select(t => t.ProductId).Distinct().ToList();
            var productNames = await db.Products.AsNoTracking()
                .Where(p => productIds.Contains(p.Id))
                .ToDictionaryAsync(p => p.Id, p => p.Name);

            var teacherIds = templates.Select(t => t.TeacherId).Distinct().ToList();
            var teacherNames = await db.Staff.AsNoTracking()
                .Where(s => teacherIds.Contains(s.Id))
                .ToDictionaryAsync(s => s.Id, s => s.Name);

            var templateIds = templates.Select(t => t.Id).ToList();
            var enrolledCounts = new Dictionary<int, int>();
            var counts = await db.ClassEnrollments.AsNoTracking()
                .Where(e => templateIds.Contains(e.TemplateId) && e.Status == EnrollmentStatus.Enrolled)
                .GroupBy(e => e.TemplateId)
                .Select(g => new { TemplateId = g.Key, Count = g.Count() })
                .ToListAsync();
            foreach (var row in counts)
            {
                enrolledCounts[row.TemplateId] = row.Count;
            }

            items = templates.Select(t => new TemplateListItem(
                t.Id,
                t.ProductId,
                productNames.GetValueOrDefault(t.ProductId),
                (int)t.DayOfWeek,
                t.StartTime,
                t.EndTime,
                t.TeacherId,
                teacherNames.GetValueOrDefault(t.TeacherId),
                t.Room,
                t.Capacity,
                t.IsActive,
                enrolledCounts.GetValueOrDefault(t.Id))).ToList();
        }

        return new Paged<TemplateListItem>(total, items);
    }

    /// <summary>
    /// POST /api/org/templates：全部字段校验通过后落库，新建模板显式激活；
    /// 归属机构由 SaveChanges 按当前员工令牌自动填充。
    /// </summary>
    public async Task<ClassTemplate> CreateAsync(CreateTemplateRequest request)
    {
        var dayOfWeek = ParseDayOfWeek(request.DayOfWeek);
        var startTime = ParseTime(request.StartTime, "startTime");
        var endTime = ParseTime(request.EndTime, "endTime");
        EnsureEndAfterStart(startTime, endTime);
        var capacity = ValidateCapacity(request.Capacity);
        await EnsureFixedProductAsync(request.ProductId);
        await EnsureTeachingStaffAsync(request.TeacherId);

        var template = new ClassTemplate
        {
            ProductId = request.ProductId,
            DayOfWeek = dayOfWeek,
            StartTime = startTime,
            EndTime = endTime,
            TeacherId = request.TeacherId,
            Room = request.Room,
            Capacity = capacity,
            // 实体默认是 false——新建模板必须显式激活，才能参与课次生成。
            IsActive = true,
            // InstitutionId 留 0：SaveChanges 按当前员工令牌的机构自动填充。
        };
        db.ClassTemplates.Add(template);
        await db.SaveChangesAsync();
        return template;
    }

    /// <summary>
    /// PUT /api/org/templates/{id}——仅更新非 null 字段；非 null 值走与创建
    /// 相同的校验（endTime&gt;startTime 以"合并后"的值判定）。修改不触碰
    /// 已生成的课次：SessionGenerationService 只按模板当前值生成未来实例。
    /// </summary>
    public async Task UpdateAsync(int id, UpdateTemplateRequest request)
    {
        var template = await db.ClassTemplates
            .SingleOrDefaultAsync(t => t.Id == id)
            ?? throw new NotFoundException("班级模板不存在");

        var dayOfWeek = request.DayOfWeek is null ? template.DayOfWeek : ParseDayOfWeek(request.DayOfWeek.Value);
        var startTime = request.StartTime is null ? template.StartTime : ParseTime(request.StartTime, "startTime");
        var endTime = request.EndTime is null ? template.EndTime : ParseTime(request.EndTime, "endTime");
        EnsureEndAfterStart(startTime, endTime);
        var capacity = request.Capacity is null ? template.Capacity : ValidateCapacity(request.Capacity);

        if (request.ProductId is not null && request.ProductId != template.ProductId)
        {
            await EnsureFixedProductAsync(request.ProductId.Value);
        }

        if (request.TeacherId is not null && request.TeacherId != template.TeacherId)
        {
            await EnsureTeachingStaffAsync(request.TeacherId.Value);
        }

        template.ProductId = request.ProductId ?? template.ProductId;
        template.DayOfWeek = dayOfWeek;
        template.StartTime = startTime;
        template.EndTime = endTime;
        template.TeacherId = request.TeacherId ?? template.TeacherId;
        template.Room = request.Room ?? template.Room;
        template.Capacity = capacity;

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// POST /{id}/deactivate|activate：停用的模板不再生成新课次（已生成的不受
    /// 影响）；重新激活后恢复生成。未知 id（含跨机构不可见）→ 404。
    /// </summary>
    public async Task SetActiveAsync(int id, bool active)
    {
        var template = await db.ClassTemplates
            .SingleOrDefaultAsync(t => t.Id == id)
            ?? throw new NotFoundException("班级模板不存在");

        template.IsActive = active;
        await db.SaveChangesAsync();
    }

    /// <summary>dayOfWeek 必须是 0（周日）到 6（周六）之间的整数。</summary>
    private static DayOfWeek ParseDayOfWeek(int dayOfWeek)
    {
        if (dayOfWeek is < 0 or > 6)
        {
            throw new BusinessRuleException("dayOfWeek 必须在 0（周日）到 6（周六）之间");
        }

        return (DayOfWeek)dayOfWeek;
    }

    /// <summary>严格 "HH:mm"（24 小时制）解析；非法值 400，绝不 500。</summary>
    private static TimeOnly ParseTime(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !TimeOnly.TryParseExact(
                value, TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
        {
            throw new BusinessRuleException($"{field} 必须是 HH:mm 格式的时间");
        }

        return time;
    }

    private static void EnsureEndAfterStart(TimeOnly startTime, TimeOnly endTime)
    {
        if (endTime <= startTime)
        {
            throw new BusinessRuleException("结束时间必须晚于开始时间");
        }
    }

    private static int? ValidateCapacity(int? capacity) =>
        capacity is null || capacity >= 1
            ? capacity
            : throw new BusinessRuleException("班级容量必须至少为 1");

    /// <summary>产品必须存在（租户过滤）且为 Fixed 型——灵活约课型不走班级排课。</summary>
    private async Task EnsureFixedProductAsync(int productId)
    {
        var isFixed = await db.Products.AsNoTracking()
            .AnyAsync(p => p.Id == productId && p.Type == ProductType.Fixed);
        if (!isFixed)
        {
            throw new BusinessRuleException("仅固定排课型产品可建班级");
        }
    }

    /// <summary>授课教师必须是本机构在职的 Teacher/Admin（跨机构 id 不可见，同样 400）。</summary>
    private async Task EnsureTeachingStaffAsync(int teacherId)
    {
        var canTeach = await db.Staff.AsNoTracking()
            .AnyAsync(s => s.Id == teacherId && s.IsActive
                && (s.Role == StaffRole.Teacher || s.Role == StaffRole.Admin));
        if (!canTeach)
        {
            throw new BusinessRuleException("授课教师必须是本机构在职的教师或管理员");
        }
    }
}
