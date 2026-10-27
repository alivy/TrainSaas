using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TrainSaas.Api.Auth;
using TrainSaas.Api.Dtos;
using TrainSaas.Api.Errors;
using TrainSaas.Api.Services;

namespace TrainSaas.Api.Controllers;

/// <summary>
/// 课次查询（Task 11，仅查询/详情）与点名写入（Task 13）。[StaffOnly]：
/// Teacher 只见自己授课的课次（详情范围外一律 404，不泄露存在性），
/// Admin/Sales/FrontDesk 见全机构（租户过滤器已限本机构）。
/// 列表先按窗口惰性生成（单次最多 28 天），再查询；from/to 为
/// yyyy-MM-dd 且跨度不得超过 60 天，非法一律 400。
/// 点名（POST {id}/attendance）的细权限（Admin 或本课次授课教师）在
/// 服务层裁决，403/404/400 经中间件映射。
/// </summary>
[ApiController]
[Route("api/org/sessions")]
[Authorize(Policy = AuthPolicies.StaffOnly)]
public class OrgSessionsController(
    SessionGenerationService sessionService,
    AttendanceService attendanceService,
    ICurrentUser currentUser) : ControllerBase
{
    /// <summary>查询窗口上限：60 天。</summary>
    private const int MaxWindowDays = 60;

    /// <summary>
    /// GET /api/org/sessions?from&amp;to →
    /// [{id,templateId,productId,productName,teacherId,teacherName,startAt,
    /// endAt,status,room,enrolledCount}]（按 startAt 升序）。先
    /// EnsureSessionsAsync(from,to) 再查询。
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<List<SessionListItem>>> List(string? from, string? to)
    {
        var fromDate = ParseDate(from, "from");
        var toDate = ParseDate(to, "to");
        if (toDate < fromDate)
        {
            throw new BusinessRuleException("to 不能早于 from");
        }

        if (toDate - fromDate > TimeSpan.FromDays(MaxWindowDays))
        {
            throw new BusinessRuleException($"查询时间范围不能超过 {MaxWindowDays} 天");
        }

        return Ok(await sessionService.ListSessionsAsync(fromDate, toDate, currentUser));
    }

    /// <summary>
    /// GET /api/org/sessions/{id} →
    /// {id,templateId,teacherId,teacherName,startAt,endAt,status,template:
    /// {productId,productName,dayOfWeek,startTime,endTime,room,capacity,
    /// isActive},roster:[{studentId,name,phone}],attendance:[{studentId,
    /// mark,operatorId,createdAt}]}。roster 只含 Enrolled 学员。
    /// </summary>
    [HttpGet("{id}")]
    public async Task<ActionResult<SessionDetail>> GetDetail(int id) =>
        Ok(await sessionService.GetSessionDetailAsync(id, currentUser));

    /// <summary>
    /// POST /api/org/sessions/{id}/attendance {marks:[{studentId,mark}]} →
    /// 200 {results:[{studentId,ok,error?}]}。mark 为区分大小写的字符串枚举
    /// Present/Leave/Absent（非法值模型绑定 400）；逐项处理、部分成功；
    /// Admin 或本课次授课教师可点名（否则 403）；任一项写入后课次转
    /// Completed。状态机与扣课/冲正见 <see cref="AttendanceService"/>。
    /// </summary>
    [HttpPost("{id}/attendance")]
    public async Task<ActionResult<AttendanceResult>> MarkAttendance(int id, AttendanceRequest request)
    {
        var marks = request.Marks?.Select(m => new MarkItem(m.StudentId, m.Mark)).ToList();
        return Ok(await attendanceService.MarkAsync(id, marks, currentUser));
    }

    /// <summary>
    /// 安全解析日期（严格 yyyy-MM-dd，本地日期）；缺失/非法值 400
    /// （BusinessRuleException），绝不 500。
    /// </summary>
    private static DateTime ParseDate(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !DateTime.TryParseExact(
                value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            throw new BusinessRuleException($"{field} 必须是 yyyy-MM-dd 格式的日期");
        }

        return parsed.Date;
    }
}
