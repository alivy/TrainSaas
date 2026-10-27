using Microsoft.EntityFrameworkCore;
using TrainSaas.Api.Auth;
using TrainSaas.Api.Data;
using TrainSaas.Api.Domain;
using TrainSaas.Api.Dtos;
using TrainSaas.Api.Errors;

namespace TrainSaas.Api.Services;

/// <summary>
/// 机构内报表（Task 15）：销售业绩与教师课量。两张表都在员工令牌下运行，
/// 数据面经租户过滤器天然限制在本机构——报表绝不 IgnoreQueryFilters
/// （家长端/公开页之外没有豁免）。
///
/// <list type="bullet">
/// <item>角色闸门在服务层裁决（路由只挂 [StaffOnly]）：销售报表 Admin 全员、
/// Sales 仅自己一行、其他角色 403；教师报表 Admin 全员、Teacher 仅自己一行、
/// 其他角色 403。Sales/Teacher 的过滤直接下推到 SQL 谓词，别人的数据既不返回
/// 也不被取回。</item>
/// <item>日期窗统一 from 含当日零点、to 排他次日零点（与流水列表同语义）。</item>
/// <item>销售口径：Status=Active 且 ConfirmedAt 在窗内的订单，按 SalesId 分组。
/// Pending/Cancelled（未确认）与 Voided（确认后作废）一律不计；无归属销售的
/// 订单不成行；窗口内零成交的员工不补零行（YAGNI）。**金额聚合绝不进 SQL**
/// （Global Constraints：SQLite 的 decimal 聚合/排序不可靠）——只把
/// (SalesId, Amount) 取回内存再 GroupBy/Count/Sum。</item>
/// <item>教师口径：completedSessions = 窗内 Status=Completed 的课次实例数；
/// presentMarks = 课次落在同一窗内的 Mark=Present 点名行数（Leave/Absent 不计，
/// 点名后课次即转 Completed，故两者窗口重合）。计数是 int，SQL Count 本可胜任，
/// 但这里同样只取回 TeacherId 列表在内存分组——避免多表 join/分组的翻译差异，
/// 口径一目了然。</item>
/// <item>行序：按 staffId/teacherId 升序（确定性，前端可自行再排序）。</item>
/// </list>
/// </summary>
public class ReportService(AppDbContext db)
{
    /// <summary>
    /// GET /api/org/reports/sales 的数据源：Admin 全员、Sales 仅自己、其他角色 403。
    /// </summary>
    public async Task<List<SalesReportRow>> SalesAsync(DateTime from, DateTime to, ICurrentUser caller)
    {
        var (windowFrom, windowToExclusive) = Window(from, to);

        // 角色闸门：Sales 只看自己（谓词下推），Admin 看全员，其余 403。
        int? onlyStaffId = caller.Role switch
        {
            StaffRole.Admin => null,
            StaffRole.Sales => RequireStaffId(caller),
            _ => throw new ForbiddenActionException("当前角色无权查看销售业绩报表"),
        };

        var query = db.Orders.AsNoTracking()
            .Where(o => o.Status == OrderStatus.Active
                && o.ConfirmedAt != null
                && o.ConfirmedAt >= windowFrom
                && o.ConfirmedAt < windowToExclusive
                && o.SalesId != null);
        if (onlyStaffId is int salesId)
        {
            query = query.Where(o => o.SalesId == salesId);
        }

        // 金额不进 SQL 聚合：只取 (SalesId, Amount) 两列，分组与求和都在内存做。
        var orders = await query.Select(o => new { o.SalesId, o.Amount }).ToListAsync();
        if (orders.Count == 0)
        {
            return [];
        }

        var grouped = orders
            .GroupBy(o => o.SalesId!.Value)
            .OrderBy(g => g.Key)
            .Select(g => (StaffId: g.Key, OrderCount: g.Count(), TotalAmount: g.Sum(o => o.Amount)))
            .ToList();

        var names = await StaffNamesAsync(grouped.Select(g => g.StaffId).ToList());
        return grouped
            .Select(g => new SalesReportRow(
                g.StaffId, names.GetValueOrDefault(g.StaffId), g.OrderCount, g.TotalAmount))
            .ToList();
    }

    /// <summary>
    /// GET /api/org/reports/teachers 的数据源：Admin 全员、Teacher 仅自己、其他角色 403。
    /// </summary>
    public async Task<List<TeacherReportRow>> TeachersAsync(DateTime from, DateTime to, ICurrentUser caller)
    {
        var (windowFrom, windowToExclusive) = Window(from, to);

        int? onlyTeacherId = caller.Role switch
        {
            StaffRole.Admin => null,
            StaffRole.Teacher => RequireStaffId(caller),
            _ => throw new ForbiddenActionException("当前角色无权查看教师课量报表"),
        };

        // 已完成课次：窗内 Status=Completed 的实例，取回 TeacherId 后内存计数。
        var completedQuery = db.ClassSessions.AsNoTracking()
            .Where(cs => cs.Status == SessionStatus.Completed
                && cs.StartAt >= windowFrom
                && cs.StartAt < windowToExclusive);
        // Present 点名：按课次 join 回同一窗口的实例，取该课次的授课教师。
        var presentQuery = db.Attendances.AsNoTracking()
            .Where(a => a.Mark == AttendanceMark.Present)
            .Join(
                db.ClassSessions.AsNoTracking(),
                a => a.ClassSessionId,
                cs => cs.Id,
                (_, cs) => cs)
            .Where(cs => cs.StartAt >= windowFrom && cs.StartAt < windowToExclusive);

        if (onlyTeacherId is int teacherId)
        {
            completedQuery = completedQuery.Where(cs => cs.TeacherId == teacherId);
            presentQuery = presentQuery.Where(cs => cs.TeacherId == teacherId);
        }

        var completedTeacherIds = await completedQuery.Select(cs => cs.TeacherId).ToListAsync();
        var presentTeacherIds = await presentQuery.Select(cs => cs.TeacherId).ToListAsync();

        var completedCounts = CountByTeacher(completedTeacherIds);
        var presentCounts = CountByTeacher(presentTeacherIds);
        var teacherIds = completedCounts.Keys.Union(presentCounts.Keys).OrderBy(id => id).ToList();
        if (teacherIds.Count == 0)
        {
            return [];
        }

        var names = await StaffNamesAsync(teacherIds);
        return teacherIds
            .Select(id => new TeacherReportRow(
                id,
                names.GetValueOrDefault(id),
                completedCounts.GetValueOrDefault(id),
                presentCounts.GetValueOrDefault(id)))
            .ToList();
    }

    /// <summary>日期窗：from 含当日零点、to 排他次日零点（to 当日整天可见）。</summary>
    private static (DateTime From, DateTime ToExclusive) Window(DateTime from, DateTime to) =>
        (from.Date, to.Date.AddDays(1));

    private static Dictionary<int, int> CountByTeacher(List<int> teacherIds) =>
        teacherIds.GroupBy(id => id).ToDictionary(g => g.Key, g => g.Count());

    /// <summary>员工姓名批量解析（无 N+1）；租户过滤器内查询。</summary>
    private async Task<Dictionary<int, string>> StaffNamesAsync(List<int> staffIds) =>
        await db.Staff.AsNoTracking()
            .Where(s => staffIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => s.Name);

    /// <summary>员工令牌必须携带 staff_id，否则视为损坏的令牌（403）。</summary>
    private static int RequireStaffId(ICurrentUser caller) =>
        caller.StaffId ?? throw new ForbiddenActionException("员工令牌缺少 staff_id");
}
