using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using TrainSaas.Api.Auth;
using TrainSaas.Api.Data;
using TrainSaas.Api.Domain;
using TrainSaas.Api.Dtos;
using TrainSaas.Api.Errors;

namespace TrainSaas.Api.Services;

/// <summary>
/// 学员管理服务。业务规则：
/// <list type="bullet">
/// <item>name 必填且 ≤50 字；phone 必填且 <c>^1\d{10}$</c>；</item>
/// <item>assignedSalesId（Admin/FrontDesk 提供时）必须是本机构在职 Sales；</item>
/// <item>Sales 创建时归属强制为自己（提供的值一律忽略）；</item>
/// <item>新建学员显式激活（实体默认 false）；</item>
/// <item>手机号不设唯一约束：同机构同号多个学员（二孩）合法（Review Focus 4）。</item>
/// </list>
///
/// 角色范围（搜索与详情与修改共用同一查询构造，规则天然一致）：
/// Admin/FrontDesk 全机构；Sales 仅 AssignedSalesId==自己；Teacher 仅
/// "在自己授课模板的班级里在班"的学员（ClassEnrollment.Status=Enrolled 且
/// ClassTemplate.TeacherId==自己）。范围外 id 与跨租户/不存在一律 404，
/// 不用 403 泄露存在性。数据面经租户过滤器天然限制在本机构。
/// </summary>
public class StudentService(AppDbContext db)
{
    private const int MaxNameLength = 50;
    private const int RecentConsumptionCount = 20;

    /// <summary>学员家长联系手机号：1 开头的 11 位数字。</summary>
    public static readonly Regex PhonePattern = new(@"^1\d{10}$", RegexOptions.Compiled);

    /// <summary>
    /// GET /api/org/students?q&amp;phone&amp;page&amp;pageSize 的数据源：角色范围内
    /// 按 Id 升序分页。q 为姓名子串（ASCII 大小写不敏感：SQLite 的 lower()
    /// 只映射 ASCII，中文原样比较，两端 ToLower 即可）；phone 为精确匹配；
    /// 两者可选、可组合。
    /// </summary>
    public async Task<Paged<StudentListItem>> SearchAsync(
        string? q, string? phone, int page, int pageSize, ICurrentUser caller)
    {
        var query = ScopeStudentsFor(caller).AsNoTracking();

        if (!string.IsNullOrWhiteSpace(q))
        {
            var keyword = q.Trim().ToLower();
            query = query.Where(s => s.Name.ToLower().Contains(keyword));
        }

        if (!string.IsNullOrWhiteSpace(phone))
        {
            var exact = phone.Trim();
            query = query.Where(s => s.Phone == exact);
        }

        var (pageNumber, pageSizeValue) = Paging.Normalize(page, pageSize);
        var total = await query.CountAsync();
        var students = await query
            .OrderBy(s => s.Id)
            .Skip((pageNumber - 1) * pageSizeValue)
            .Take(pageSizeValue)
            .ToListAsync();

        return new Paged<StudentListItem>(total, await ToListItemsAsync(students));
    }

    /// <summary>
    /// GET /api/org/students/{id}：详情聚合
    /// {student, packs, orders, recentConsumptions, enrollments}。范围外的
    /// id（越权/跨租户/不存在）一律 <see cref="NotFoundException"/> 404。
    /// 后期任务（SessionPack/Order/Consumption 服务）尚未交付，这里直接读表。
    /// </summary>
    public async Task<StudentDetail> GetDetailAsync(int id, ICurrentUser caller)
    {
        var student = await ScopeStudentsFor(caller).AsNoTracking()
            .SingleOrDefaultAsync(s => s.Id == id)
            ?? throw new NotFoundException("学员不存在");

        var packs = await db.SessionPacks.AsNoTracking()
            .Where(sp => sp.StudentId == id)
            .OrderBy(sp => sp.Id)
            .ToListAsync();
        var orders = await db.Orders.AsNoTracking()
            .Where(o => o.StudentId == id)
            .OrderByDescending(o => o.Id)
            .ToListAsync();
        var consumptions = await db.Consumptions.AsNoTracking()
            .Where(c => c.StudentId == id)
            .OrderByDescending(c => c.CreatedAt)
            .ThenByDescending(c => c.Id)
            .Take(RecentConsumptionCount)
            .ToListAsync();
        var enrollments = await db.ClassEnrollments.AsNoTracking()
            .Where(e => e.StudentId == id)
            .OrderBy(e => e.Id)
            .ToListAsync();

        // 名称解析（产品/员工）在内存中做，避免多表 join 的翻译差异。
        var packProductIds = consumptions.Select(c => c.SessionPackId).Distinct().ToList();
        var packIdToProductId = packProductIds.Count == 0
            ? new Dictionary<int, int>()
            : (await db.SessionPacks.AsNoTracking()
                .Where(sp => packProductIds.Contains(sp.Id))
                .Select(sp => new { sp.Id, sp.ProductId })
                .ToListAsync())
                .ToDictionary(x => x.Id, x => x.ProductId);

        var productIds = packs.Select(sp => sp.ProductId)
            .Concat(orders.Select(o => o.ProductId))
            .Concat(packIdToProductId.Values)
            .Distinct()
            .ToList();
        var productNames = productIds.Count == 0
            ? new Dictionary<int, string>()
            : await db.Products.AsNoTracking()
                .Where(p => productIds.Contains(p.Id))
                .ToDictionaryAsync(p => p.Id, p => p.Name);

        var templateIds = enrollments.Select(e => e.TemplateId).Distinct().ToList();
        var templates = templateIds.Count == 0
            ? new List<ClassTemplate>()
            : await db.ClassTemplates.AsNoTracking()
                .Where(t => templateIds.Contains(t.Id))
                .ToListAsync();
        var templateById = templates.ToDictionary(t => t.Id);

        var teacherIds = templates.Select(t => t.TeacherId).Distinct().ToList();
        var teacherNames = teacherIds.Count == 0
            ? new Dictionary<int, string>()
            : await db.Staff.AsNoTracking()
                .Where(s => teacherIds.Contains(s.Id))
                .ToDictionaryAsync(s => s.Id, s => s.Name);

        var enrollmentItems = new List<StudentEnrollmentItem>();
        foreach (var enrollment in enrollments)
        {
            if (!templateById.TryGetValue(enrollment.TemplateId, out var template))
            {
                continue;
            }

            enrollmentItems.Add(new StudentEnrollmentItem(
                enrollment.TemplateId,
                template.DayOfWeek.ToString(),
                template.StartTime,
                template.EndTime,
                teacherNames.GetValueOrDefault(template.TeacherId),
                enrollment.Status.ToString()));
        }

        return new StudentDetail(
            ToResponse(student),
            packs.Select(sp => new StudentPackItem(
                sp.ProductId,
                productNames.GetValueOrDefault(sp.ProductId),
                sp.TotalSessions,
                sp.RemainingSessions,
                sp.Status.ToString())).ToList(),
            orders.Select(o => new StudentOrderItem(
                o.Id,
                o.OrderNo,
                o.ProductId,
                productNames.GetValueOrDefault(o.ProductId),
                o.Amount,
                o.Status.ToString(),
                o.CreatedAt)).ToList(),
            consumptions.Select(c => new StudentConsumptionItem(
                c.Id,
                c.Kind.ToString(),
                c.Direction,
                packIdToProductId.TryGetValue(c.SessionPackId, out var productId)
                    ? productNames.GetValueOrDefault(productId)
                    : null,
                c.CreatedAt)).ToList(),
            enrollmentItems);
    }

    /// <summary>
    /// POST /api/org/students {name,phone,gender?,birthday?,assignedSalesId?,notes?}：
    /// 新学员显式激活；Sales 角色时 AssignedSalesId 强制为自己；归属机构由
    /// SaveChanges 按当前员工令牌自动填充。
    /// </summary>
    public async Task<Student> CreateAsync(CreateStudentRequest request, ICurrentUser caller)
    {
        var name = ValidateName(request.Name);
        var phone = ValidatePhone(request.Phone);

        int? assignedSalesId;
        if (caller.Role == StaffRole.Sales)
        {
            // Sales 创建的学员只能归属自己：提供的 assignedSalesId 一律忽略。
            assignedSalesId = RequireStaffId(caller);
        }
        else
        {
            assignedSalesId = request.AssignedSalesId;
            if (assignedSalesId is not null)
                await EnsureAssignableSalesAsync(assignedSalesId.Value);
        }

        var student = new Student
        {
            Name = name,
            Phone = phone,
            Gender = request.Gender,
            Birthday = request.Birthday,
            AssignedSalesId = assignedSalesId,
            Notes = request.Notes,
            // 实体默认是 false——新学员必须显式激活。
            IsActive = true,
            // InstitutionId 留 0：SaveChanges 按当前员工令牌的机构自动填充。
        };
        db.Students.Add(student);
        await db.SaveChangesAsync();
        return student;
    }

    /// <summary>
    /// PUT /api/org/students/{id}（AdminSalesFrontDesk）。字段权限按角色：
    /// Admin 任意字段（非 null 值走与创建相同的校验）；Sales 仅本人名下
    /// （范围外 404）且只能改 gender/birthday/notes，提供
    /// name/phone/assignedSalesId → <see cref="ForbiddenActionException"/> 403；
    /// FrontDesk 仅 notes，提供其它字段 → 403。
    /// </summary>
    public async Task UpdateAsync(int id, UpdateStudentRequest request, ICurrentUser caller)
    {
        // 与搜索/详情同一套范围：越权/跨租户/不存在一律 404。
        var student = await ScopeStudentsFor(caller)
            .SingleOrDefaultAsync(s => s.Id == id)
            ?? throw new NotFoundException("学员不存在");

        switch (caller.Role)
        {
            case StaffRole.Admin:
                if (request.Name is not null)
                    student.Name = ValidateName(request.Name);
                if (request.Phone is not null)
                    student.Phone = ValidatePhone(request.Phone);
                if (request.Gender is not null)
                    student.Gender = request.Gender;
                if (request.Birthday is not null)
                    student.Birthday = request.Birthday;
                if (request.Notes is not null)
                    student.Notes = request.Notes;
                if (request.AssignedSalesId is not null)
                {
                    await EnsureAssignableSalesAsync(request.AssignedSalesId.Value);
                    student.AssignedSalesId = request.AssignedSalesId;
                }
                break;

            case StaffRole.Sales:
                if (request.Name is not null || request.Phone is not null || request.AssignedSalesId is not null)
                    throw new ForbiddenActionException("销售只能修改学员的性别、生日和备注");
                if (request.Gender is not null)
                    student.Gender = request.Gender;
                if (request.Birthday is not null)
                    student.Birthday = request.Birthday;
                if (request.Notes is not null)
                    student.Notes = request.Notes;
                break;

            case StaffRole.FrontDesk:
                if (request.Name is not null || request.Phone is not null || request.Gender is not null
                    || request.Birthday is not null || request.AssignedSalesId is not null)
                    throw new ForbiddenActionException("前台只能修改学员备注");
                if (request.Notes is not null)
                    student.Notes = request.Notes;
                break;

            default:
                throw new ForbiddenActionException("当前角色无权修改学员");
        }

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// 角色范围查询（搜索/详情/修改共用）：Admin/FrontDesk 全机构（租户过滤器
    /// 已限本机构）；Sales 仅 AssignedSalesId==自己；Teacher 仅"在自己授课模板
    /// 的班级里在班"的学员。
    /// </summary>
    private IQueryable<Student> ScopeStudentsFor(ICurrentUser caller) => caller.Role switch
    {
        StaffRole.Admin or StaffRole.FrontDesk => db.Students,
        StaffRole.Sales => ScopedBySales(caller),
        StaffRole.Teacher => ScopedByTeacher(caller),
        _ => throw new ForbiddenActionException("当前角色无权访问学员数据"),
    };

    private IQueryable<Student> ScopedBySales(ICurrentUser caller)
    {
        var salesId = RequireStaffId(caller);
        return db.Students.Where(s => s.AssignedSalesId == salesId);
    }

    private IQueryable<Student> ScopedByTeacher(ICurrentUser caller)
    {
        var teacherId = RequireStaffId(caller);
        return db.Students.Where(s => db.ClassEnrollments
            .Where(e => e.Status == EnrollmentStatus.Enrolled
                && db.ClassTemplates.Any(t => t.Id == e.TemplateId && t.TeacherId == teacherId))
            .Select(e => e.StudentId)
            .Contains(s.Id));
    }

    /// <summary>assignedSalesId 必须是本机构在职 Sales（跨机构 id 被租户过滤器隐藏，同样 400）。</summary>
    private async Task EnsureAssignableSalesAsync(int salesId)
    {
        var assignable = await db.Staff.AsNoTracking()
            .AnyAsync(s => s.Id == salesId && s.Role == StaffRole.Sales && s.IsActive);
        if (!assignable)
            throw new BusinessRuleException("归属销售不存在、已停用或不是销售角色");
    }

    private static string ValidateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new BusinessRuleException("学员姓名不能为空");
        if (name.Length > MaxNameLength)
            throw new BusinessRuleException($"学员姓名不能超过 {MaxNameLength} 个字符");

        return name;
    }

    private static string ValidatePhone(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone))
            throw new BusinessRuleException("学员手机号不能为空");
        if (!PhonePattern.IsMatch(phone))
            throw new BusinessRuleException("学员手机号格式无效");

        return phone;
    }

    /// <summary>员工令牌必须携带 staff_id，否则视为损坏的令牌（403）。</summary>
    private static int RequireStaffId(ICurrentUser caller) =>
        caller.StaffId ?? throw new ForbiddenActionException("员工令牌缺少 staff_id");

    /// <summary>列表项的 assignedSalesName 批量解析（本机构员工，经租户过滤器）。</summary>
    private async Task<List<StudentListItem>> ToListItemsAsync(List<Student> students)
    {
        var salesIds = students
            .Select(s => s.AssignedSalesId)
            .Where(id => id is not null)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();
        var salesNames = salesIds.Count == 0
            ? new Dictionary<int, string>()
            : await db.Staff.AsNoTracking()
                .Where(s => salesIds.Contains(s.Id))
                .ToDictionaryAsync(s => s.Id, s => s.Name);

        return students.Select(s => new StudentListItem(
            s.Id,
            s.Name,
            s.Phone,
            s.Gender,
            s.Birthday,
            s.AssignedSalesId,
            s.AssignedSalesId is int salesId ? salesNames.GetValueOrDefault(salesId) : null,
            s.IsActive,
            s.CreatedAt)).ToList();
    }

    private static StudentResponse ToResponse(Student student) =>
        new(student.Id, student.Name, student.Phone, student.Gender, student.Birthday,
            student.AssignedSalesId, student.Notes, student.IsActive, student.CreatedAt);
}
