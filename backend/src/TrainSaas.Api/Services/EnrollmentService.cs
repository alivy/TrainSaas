using Microsoft.EntityFrameworkCore;
using TrainSaas.Api.Auth;
using TrainSaas.Api.Data;
using TrainSaas.Api.Domain;
using TrainSaas.Api.Dtos;
using TrainSaas.Api.Errors;

namespace TrainSaas.Api.Services;

/// <summary>
/// 班级报名服务（Task 12：报班/退班/名册）。行复用语义：同一
/// (TemplateId,StudentId) 至多一行——曾退班的学员重报翻回 Enrolled 并更新
/// EnrolledById/EnrolledAt，绝不另起新行；唯一索引 (TemplateId,StudentId)
/// 是并发双报的数据库兜底。所有查询经租户过滤器，未知/跨机构的模板/学员
/// 一律 404（不泄露存在性）。
/// <list type="bullet">
/// <item>报名（R9 校验顺序）：模板存在且 IsActive 否则 400 "班级已停用"；
/// 学员存在且 IsActive 否则 400 "学员已停用"；已在班（Enrolled）→ 409；
/// <c>Capacity.HasValue &amp;&amp; 在班数 &gt;= Capacity</c> → 400 "班级已满"
/// （插入前检查，失败不落行；重报同样占名额）。</item>
/// <item>退班：无在班（Enrolled）记录 → 404；置 Withdrawn，行保留为历史。</item>
/// <item>名册：Admin 任意模板；该模板授课教师（比对令牌 staff_id 与模板
/// TeacherId）可读自己班；其他角色一律 403。仅 Enrolled、按 studentId 升序。</item>
/// <item>并发兜底：双报竞态输家的插入被唯一索引拒绝（DbUpdateException），
/// 捕获后重读该行——已 Enrolled 则收敛为 409，否则原样重抛。</item>
/// </list>
/// </summary>
public class EnrollmentService(AppDbContext db)
{
    /// <summary>
    /// POST /api/org/templates/{id}/enrollments（AdminOnly）。校验顺序：
    /// 模板 → 学员 → 在班查重 → 容量，任何一步失败都不落行。曾退班 →
    /// 复用原行翻回 Enrolled（同 id）。归属机构由 SaveChanges 按当前员工
    /// 令牌自动填充。
    /// </summary>
    public async Task<ClassEnrollment> EnrollAsync(int templateId, int studentId, int operatorId)
    {
        // 租户过滤的解析：跨机构/未知模板一律 404（与不存在同态）。
        var template = await db.ClassTemplates.AsNoTracking()
            .SingleOrDefaultAsync(t => t.Id == templateId)
            ?? throw new NotFoundException("班级模板不存在");
        if (!template.IsActive)
        {
            throw new BusinessRuleException("班级已停用");
        }

        var student = await db.Students.AsNoTracking()
            .SingleOrDefaultAsync(s => s.Id == studentId)
            ?? throw new NotFoundException("学员不存在");
        if (!student.IsActive)
        {
            throw new BusinessRuleException("学员已停用");
        }

        var existing = await db.ClassEnrollments
            .SingleOrDefaultAsync(e => e.TemplateId == templateId && e.StudentId == studentId);
        if (existing is { Status: EnrollmentStatus.Enrolled })
        {
            throw new ConflictException("学员已在该班级中");
        }

        // 容量在写入前检查：在班数只数 Enrolled（退班的历史行不占名额）；
        // 重报与首报同样占一个名额。
        if (template.Capacity is int capacity)
        {
            var enrolledCount = await db.ClassEnrollments.AsNoTracking()
                .CountAsync(e => e.TemplateId == templateId && e.Status == EnrollmentStatus.Enrolled);
            if (enrolledCount >= capacity)
            {
                throw new BusinessRuleException("班级已满");
            }
        }

        if (existing is not null)
        {
            // 曾退班：复用原行翻回 Enrolled，登记本次操作人与时间。
            existing.Status = EnrollmentStatus.Enrolled;
            existing.EnrolledById = operatorId;
            existing.EnrolledAt = DateTime.Now;
            await db.SaveChangesAsync();
            return existing;
        }

        var enrollment = new ClassEnrollment
        {
            TemplateId = templateId,
            StudentId = studentId,
            EnrolledById = operatorId,
            EnrolledAt = DateTime.Now,
            Status = EnrollmentStatus.Enrolled,
            // InstitutionId 留 0：SaveChanges 按当前员工令牌自动填充。
        };
        db.ClassEnrollments.Add(enrollment);
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // 并发赢家抢先提交了同一 (TemplateId,StudentId)（事务已回滚）：
            // 丢弃冲突实体重读该行——已 Enrolled 则收敛为 409，否则重抛。
            db.ChangeTracker.Clear();
            var raced = await db.ClassEnrollments.AsNoTracking()
                .SingleOrDefaultAsync(e => e.TemplateId == templateId && e.StudentId == studentId);
            if (raced is { Status: EnrollmentStatus.Enrolled })
            {
                throw new ConflictException("学员已在该班级中");
            }

            throw;
        }

        return enrollment;
    }

    /// <summary>
    /// DELETE /api/org/templates/{id}/enrollments/{studentId}（AdminOnly）。
    /// 仅翻转当前 Enrolled 行；无在班记录（从未报班/已退班/跨机构不可见）
    /// → 404。行保留为历史，重报时复用。
    /// </summary>
    public async Task WithdrawAsync(int templateId, int studentId)
    {
        // 租户过滤器内的在班行：跨机构模板/学员与本机构不存在的行同态 404。
        var enrollment = await db.ClassEnrollments
            .SingleOrDefaultAsync(e => e.TemplateId == templateId
                && e.StudentId == studentId
                && e.Status == EnrollmentStatus.Enrolled)
            ?? throw new NotFoundException("学员不在该班级中");

        enrollment.Status = EnrollmentStatus.Withdrawn;
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// GET /api/org/templates/{id}/enrollments 的数据源：Admin 任意模板；
    /// 该模板授课教师（模板 TeacherId == 令牌 staff_id）可读自己班；其他人
    /// 一律 403。仅 Enrolled 行，按 studentId 升序。
    /// </summary>
    public async Task<List<RosterItem>> GetRosterAsync(int templateId, ICurrentUser caller)
    {
        var template = await db.ClassTemplates.AsNoTracking()
            .SingleOrDefaultAsync(t => t.Id == templateId)
            ?? throw new NotFoundException("班级模板不存在");

        if (caller.Role != StaffRole.Admin)
        {
            var staffId = caller.StaffId
                ?? throw new ForbiddenActionException("员工令牌缺少 staff_id");
            if (template.TeacherId != staffId)
            {
                throw new ForbiddenActionException("只有管理员或班级授课教师可以查看班级名册");
            }
        }

        var enrollments = await db.ClassEnrollments.AsNoTracking()
            .Where(e => e.TemplateId == templateId && e.Status == EnrollmentStatus.Enrolled)
            .OrderBy(e => e.StudentId)
            .ToListAsync();

        var studentIds = enrollments.Select(e => e.StudentId).Distinct().ToList();
        var studentsById = studentIds.Count == 0
            ? new Dictionary<int, Student>()
            : await db.Students.AsNoTracking()
                .Where(s => studentIds.Contains(s.Id))
                .ToDictionaryAsync(s => s.Id);

        return enrollments
            .Select(e => studentsById.TryGetValue(e.StudentId, out var student)
                ? new RosterItem(e.StudentId, student.Name, student.Phone, e.Status.ToString(), e.EnrolledAt)
                : null)
            .Where(item => item is not null)
            .Select(item => item!)
            .ToList();
    }
}
