using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TrainSaas.Api.Auth;
using TrainSaas.Api.Dtos;
using TrainSaas.Api.Errors;
using TrainSaas.Api.Services;

namespace TrainSaas.Api.Controllers;

/// <summary>
/// 机构内报表（Task 15）：销售业绩与教师课量。类级 [StaffOnly]，角色细规则
/// （销售报表 Admin 全员 / Sales 仅自己一行；教师报表 Admin 全员 / Teacher 仅
/// 自己一行；其余角色一律 403）在 <see cref="ReportService"/> 内裁决。
/// from/to 必填且严格 yyyy-MM-dd（缺失或非法 400），from&gt;to 400 —— 安全解析，
/// 绝不 500。窗口语义：from 含当日零点、to 排他次日零点。
/// </summary>
[ApiController]
[Route("api/org/reports")]
[Authorize(Policy = AuthPolicies.StaffOnly)]
public class OrgReportsController(ReportService reportService, ICurrentUser currentUser) : ControllerBase
{
    /// <summary>
    /// GET /api/org/reports/sales?from&amp;to →
    /// [{staffId,staffName,orderCount,totalAmount}]（按 staffId 升序）。
    /// 只计 Status=Active 且 ConfirmedAt 在窗内的订单；金额在内存中聚合。
    /// </summary>
    [HttpGet("sales")]
    public async Task<ActionResult<List<SalesReportRow>>> Sales(string? from, string? to)
    {
        var (fromDate, toDate) = ParseWindow(from, to);
        return Ok(await reportService.SalesAsync(fromDate, toDate, currentUser));
    }

    /// <summary>
    /// GET /api/org/reports/teachers?from&amp;to →
    /// [{teacherId,teacherName,completedSessions,presentMarks}]（按 teacherId 升序）。
    /// </summary>
    [HttpGet("teachers")]
    public async Task<ActionResult<List<TeacherReportRow>>> Teachers(string? from, string? to)
    {
        var (fromDate, toDate) = ParseWindow(from, to);
        return Ok(await reportService.TeachersAsync(fromDate, toDate, currentUser));
    }

    /// <summary>两张报表共用的窗口解析：必填、严格 yyyy-MM-dd、from ≤ to。</summary>
    private static (DateTime From, DateTime To) ParseWindow(string? from, string? to)
    {
        var fromDate = ParseDate(from, "from");
        var toDate = ParseDate(to, "to");
        if (toDate < fromDate)
        {
            throw new BusinessRuleException("to 不能早于 from");
        }

        return (fromDate, toDate);
    }

    /// <summary>
    /// 安全解析必填日期（严格 yyyy-MM-dd，本地日期）：缺失/非法值 → 400
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
