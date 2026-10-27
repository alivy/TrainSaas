using Microsoft.EntityFrameworkCore;
using TrainSaas.Api.Auth;
using TrainSaas.Api.Data;
using TrainSaas.Api.Domain;
using TrainSaas.Api.Dtos;
using TrainSaas.Api.Errors;

namespace TrainSaas.Api.Services;

/// <summary>
/// 课次惰性生成与查询（Task 11）。核心裁定：
/// <list type="bullet">
/// <item><see cref="EnsureSessionsAsync"/>：to 钳制为 Min(to, from+28 天)；
/// 对每个 IsActive 模板，窗口内每个匹配 DayOfWeek 的本地日期生成
/// ClassSession{TeacherId=模板教师, StartAt=date+StartTime, EndAt=date+EndTime,
/// Status=Scheduled}；已存在 (TemplateId,StartAt) 则跳过（幂等），单次
/// SaveChanges 批量落库。模板查询经租户过滤器天然限制在本机构。</item>
/// <item>列表：先 EnsureSessionsAsync(from,to) 再查询；Teacher 只见
/// TeacherId==自己的课次，Admin/Sales/FrontDesk 见全机构；按 startAt 升序，
/// 名称/教室/在班数批量解析（无 N+1）。</item>
/// <item>详情：Teacher 访问他人课次与未知/跨机构 id 同样 404（不泄露存在性）；
/// roster 直接查 ClassEnrollments（仅 Enrolled）关联学员；attendance 为既有
/// 点名行（写入口是 Task 13）。</item>
/// <item>全部 DateTime 为服务器本地时间（Global Constraints，不做 UTC 换算）。</item>
/// <item>并发（R11）：唯一索引 (TemplateId,StartAt) 是数据库兜底；输家的
/// DbUpdateException 被捕获并按最新库态单次重试收敛，无异常逃逸。</item>
/// <item>R15：<see cref="EnsureSessionsForInstitutionAsync"/> 供家长端（无租户
/// 上下文）按机构触发生成——同款钳制/幂等/收敛，但查询走 IgnoreQueryFilters +
/// 显式机构谓词、新行显式落 InstitutionId。<see cref="EnsureSessionsAsync"/>
/// （员工路径）行为零改动，两者共用同一份生成算法（私有 AddMissingSessionsAsync）。</item>
/// </list>
/// </summary>
public class SessionGenerationService(AppDbContext db)
{
    /// <summary>单次惰性生成的最大窗口：28 天（钳制 to）。</summary>
    private const int GenerationWindowDays = 28;

    /// <summary>
    /// 确保窗口内的课次存在（幂等）。调用方（会话查询）负责把跨度限制在
    /// 60 天内；本方法再钳制生成窗口到 28 天，避免一次请求生成过多行。
    /// 并发安全（R11）：两个请求同时首化同一窗口时，唯一索引
    /// (TemplateId,StartAt) 把输家的插入变成 DbUpdateException——捕获后按
    /// 最新库态重算缺口补一轮（单次重试），与赢家收敛到同一终态，无异常逃逸。
    /// </summary>
    public async Task EnsureSessionsAsync(DateTime from, DateTime to)
    {
        var effectiveTo = to < from.AddDays(GenerationWindowDays) ? to : from.AddDays(GenerationWindowDays);

        if (!await AddMissingSessionsAsync(from, effectiveTo))
        {
            return;
        }

        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // 并发赢家已抢先提交了相同 (TemplateId,StartAt)（事务已回滚）：
            // 丢弃本批冲突实体，按最新库态重算仍缺失的实例再插一轮。
            db.ChangeTracker.Clear();
            await AddMissingSessionsAsync(from, effectiveTo);
            await db.SaveChangesAsync();
        }
    }

    /// <summary>
    /// R15：家长端触发的按机构惰性生成。语义与 <see cref="EnsureSessionsAsync"/>
    /// 完全一致（同款 28 天窗口钳制、(TemplateId,StartAt) 幂等跳过、唯一索引 +
    /// DbUpdateException 单次重试收敛），差别只在数据面：家长令牌没有租户上下文，
    /// 所以模板与既有实例的查询走 <c>IgnoreQueryFilters()</c> + 显式
    /// <c>InstitutionId == institutionId</c> 谓词（家长端豁免纪律），且新建实例
    /// **显式落 InstitutionId**——依赖 SaveChanges 的自动填充在无租户上下文时会抛
    /// ForbiddenActionException。机构 id 由调用方从已验证归属的孩子取得，绝不取自
    /// 请求参数。
    /// </summary>
    public async Task EnsureSessionsForInstitutionAsync(int institutionId, DateTime from, DateTime to)
    {
        var effectiveTo = to < from.AddDays(GenerationWindowDays) ? to : from.AddDays(GenerationWindowDays);

        if (!await AddMissingSessionsAsync(from, effectiveTo, institutionId))
        {
            return;
        }

        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // 与员工路径同款收敛：并发赢家（可能是机构侧的 EnsureSessionsAsync）
            // 已提交相同 (TemplateId,StartAt)，丢弃本批冲突实体后按最新库态补一轮。
            db.ChangeTracker.Clear();
            await AddMissingSessionsAsync(from, effectiveTo, institutionId);
            await db.SaveChangesAsync();
        }
    }

    /// <summary>
    /// 把窗口内仍缺失的课次加入变更跟踪器（不落库）。返回是否加入了任何
    /// 实体；已存在 (TemplateId,StartAt) 的键（含本批内重复）一律跳过。
    ///
    /// <paramref name="institutionId"/> 为 null 时是员工路径（既有行为，零改动）：
    /// 模板/实例查询经租户过滤器天然限定本机构，新行 InstitutionId 留 0 由
    /// SaveChanges 按令牌自动填充。非 null 时是家长路径（R15）：查询改为
    /// IgnoreQueryFilters + 显式机构谓词，新行显式落该机构 id。
    /// </summary>
    private async Task<bool> AddMissingSessionsAsync(
        DateTime from, DateTime effectiveTo, int? institutionId = null)
    {
        List<ClassTemplate> templates;
        if (institutionId is not null)
        {
            // 家长上下文无租户：IgnoreQueryFilters 必须紧跟显式机构谓词。
            templates = await db.ClassTemplates.IgnoreQueryFilters().AsNoTracking()
                .Where(t => t.InstitutionId == institutionId.Value && t.IsActive)
                .ToListAsync();
        }
        else
        {
            templates = await db.ClassTemplates.AsNoTracking()
                .Where(t => t.IsActive)
                .ToListAsync();
        }

        var templateIds = templates.Select(t => t.Id).Distinct().ToList();
        var existingKeys = new HashSet<(int TemplateId, DateTime StartAt)>();
        if (templateIds.Count > 0)
        {
            var windowEndExclusive = effectiveTo.Date.AddDays(1);
            var existingQuery = db.ClassSessions.AsNoTracking()
                .Where(cs => templateIds.Contains(cs.TemplateId)
                    && cs.StartAt >= from.Date
                    && cs.StartAt < windowEndExclusive);
            if (institutionId is not null)
            {
                existingQuery = existingQuery
                    .IgnoreQueryFilters()
                    .Where(cs => cs.InstitutionId == institutionId.Value);
            }

            var existing = await existingQuery
                .Select(cs => new { cs.TemplateId, cs.StartAt })
                .ToListAsync();
            foreach (var row in existing)
            {
                existingKeys.Add((row.TemplateId, row.StartAt));
            }
        }

        var added = false;
        foreach (var template in templates)
        {
            for (var date = from.Date; date <= effectiveTo.Date; date = date.AddDays(1))
            {
                if (date.DayOfWeek != template.DayOfWeek)
                {
                    continue;
                }

                var startAt = date.Add(template.StartTime.ToTimeSpan());
                if (!existingKeys.Add((template.Id, startAt)))
                {
                    // 已存在（或本批内重复）：跳过，保证幂等。
                    continue;
                }

                db.ClassSessions.Add(new ClassSession
                {
                    TemplateId = template.Id,
                    TeacherId = template.TeacherId,
                    StartAt = startAt,
                    EndAt = date.Add(template.EndTime.ToTimeSpan()),
                    Status = SessionStatus.Scheduled,
                    // 员工路径留 0：SaveChanges 按当前员工令牌自动填充（既有行为）。
                    // 家长路径无租户上下文，自动填充会抛 403，故显式落机构 id（R15）。
                    InstitutionId = institutionId ?? 0,
                });
                added = true;
            }
        }

        return added;
    }

    /// <summary>
    /// GET /api/org/sessions?from&amp;to 的数据源：先惰性生成窗口内课次，再按
    /// 角色查询。Teacher 只见 TeacherId==自己；其余角色见全机构（租户过滤器
    /// 已限本机构）。
    /// </summary>
    public async Task<List<SessionListItem>> ListSessionsAsync(
        DateTime from, DateTime to, ICurrentUser caller)
    {
        await EnsureSessionsAsync(from, to);

        var query = db.ClassSessions.AsNoTracking()
            .Where(cs => cs.StartAt >= from.Date && cs.StartAt < to.Date.AddDays(1));

        if (caller.Role == StaffRole.Teacher)
        {
            var teacherId = RequireStaffId(caller);
            query = query.Where(cs => cs.TeacherId == teacherId);
        }

        var sessions = await query
            .OrderBy(cs => cs.StartAt)
            .ThenBy(cs => cs.Id)
            .ToListAsync();

        var templateIds = sessions.Select(cs => cs.TemplateId).Distinct().ToList();
        var templates = templateIds.Count == 0
            ? new List<ClassTemplate>()
            : await db.ClassTemplates.AsNoTracking()
                .Where(t => templateIds.Contains(t.Id))
                .ToListAsync();
        var templateById = templates.ToDictionary(t => t.Id);

        var productIds = templates.Select(t => t.ProductId).Distinct().ToList();
        var productNames = productIds.Count == 0
            ? new Dictionary<int, string>()
            : await db.Products.AsNoTracking()
                .Where(p => productIds.Contains(p.Id))
                .ToDictionaryAsync(p => p.Id, p => p.Name);

        var teacherIds = sessions.Select(cs => cs.TeacherId).Distinct().ToList();
        var teacherNames = teacherIds.Count == 0
            ? new Dictionary<int, string>()
            : await db.Staff.AsNoTracking()
                .Where(s => teacherIds.Contains(s.Id))
                .ToDictionaryAsync(s => s.Id, s => s.Name);

        var enrolledCounts = new Dictionary<int, int>();
        if (templateIds.Count > 0)
        {
            var counts = await db.ClassEnrollments.AsNoTracking()
                .Where(e => templateIds.Contains(e.TemplateId) && e.Status == EnrollmentStatus.Enrolled)
                .GroupBy(e => e.TemplateId)
                .Select(g => new { TemplateId = g.Key, Count = g.Count() })
                .ToListAsync();
            foreach (var row in counts)
            {
                enrolledCounts[row.TemplateId] = row.Count;
            }
        }

        return sessions.Select(cs =>
        {
            var template = templateById.GetValueOrDefault(cs.TemplateId);
            return new SessionListItem(
                cs.Id,
                cs.TemplateId,
                template?.ProductId ?? 0,
                template is null ? null : productNames.GetValueOrDefault(template.ProductId),
                cs.TeacherId,
                teacherNames.GetValueOrDefault(cs.TeacherId),
                cs.StartAt,
                cs.EndAt,
                cs.Status.ToString(),
                template?.Room,
                enrolledCounts.GetValueOrDefault(cs.TemplateId));
        }).ToList();
    }

    /// <summary>
    /// GET /api/org/sessions/{id}：课次字段 + 内嵌模板信息 + roster（仅 Enrolled）
    /// + 既有点名。Teacher 仅限自己授课的课次，范围外与未知/跨机构 id 一律 404。
    /// </summary>
    public async Task<SessionDetail> GetSessionDetailAsync(int id, ICurrentUser caller)
    {
        var query = db.ClassSessions.AsNoTracking().AsQueryable();
        if (caller.Role == StaffRole.Teacher)
        {
            var teacherId = RequireStaffId(caller);
            query = query.Where(cs => cs.TeacherId == teacherId);
        }

        var session = await query.SingleOrDefaultAsync(cs => cs.Id == id)
            ?? throw new NotFoundException("课次不存在");

        var template = await db.ClassTemplates.AsNoTracking()
            .SingleOrDefaultAsync(t => t.Id == session.TemplateId);

        var teacherName = await db.Staff.AsNoTracking()
            .Where(s => s.Id == session.TeacherId)
            .Select(s => s.Name)
            .SingleOrDefaultAsync();

        // roster：直接查 ClassEnrollments（Enrolled）再关联学员；报名服务
        // （Task 12）落地后可重构此处。
        var enrollments = await db.ClassEnrollments.AsNoTracking()
            .Where(e => e.TemplateId == session.TemplateId && e.Status == EnrollmentStatus.Enrolled)
            .OrderBy(e => e.StudentId)
            .ToListAsync();
        var studentIds = enrollments.Select(e => e.StudentId).Distinct().ToList();
        var studentsById = studentIds.Count == 0
            ? new Dictionary<int, Student>()
            : await db.Students.AsNoTracking()
                .Where(s => studentIds.Contains(s.Id))
                .ToDictionaryAsync(s => s.Id);
        var roster = enrollments
            .Select(e => studentsById.TryGetValue(e.StudentId, out var student)
                ? new SessionRosterItem(student.Id, student.Name, student.Phone)
                : null)
            .Where(item => item is not null)
            .Select(item => item!)
            .ToList();

        var attendance = await db.Attendances.AsNoTracking()
            .Where(a => a.ClassSessionId == session.Id)
            .OrderBy(a => a.Id)
            .Select(a => new SessionAttendanceItem(a.StudentId, a.Mark.ToString(), a.OperatorId, a.CreatedAt))
            .ToListAsync();

        SessionTemplateInfo? templateInfo = null;
        if (template is not null)
        {
            var productName = await db.Products.AsNoTracking()
                .Where(p => p.Id == template.ProductId)
                .Select(p => p.Name)
                .SingleOrDefaultAsync();
            templateInfo = new SessionTemplateInfo(
                template.ProductId,
                productName,
                (int)template.DayOfWeek,
                template.StartTime,
                template.EndTime,
                template.Room,
                template.Capacity,
                template.IsActive);
        }

        return new SessionDetail(
            session.Id,
            session.TemplateId,
            session.TeacherId,
            teacherName,
            session.StartAt,
            session.EndAt,
            session.Status.ToString(),
            templateInfo,
            roster,
            attendance);
    }

    /// <summary>员工令牌必须携带 staff_id，否则视为损坏的令牌（403）。</summary>
    private static int RequireStaffId(ICurrentUser caller) =>
        caller.StaffId ?? throw new ForbiddenActionException("员工令牌缺少 staff_id");
}
