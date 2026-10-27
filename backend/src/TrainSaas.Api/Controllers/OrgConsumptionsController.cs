using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TrainSaas.Api.Auth;
using TrainSaas.Api.Domain;
using TrainSaas.Api.Dtos;
using TrainSaas.Api.Errors;
using TrainSaas.Api.Services;

namespace TrainSaas.Api.Controllers;

/// <summary>
/// 课时流水（Task 14）：到店核销、流水查询、撤销冲正。类级 [StaffOnly]；
/// walk-in 追加 [AdminOrFrontDesk]（Teacher/Sales 403）。列表与撤销的角色
/// 细规则（Sales 仅名下学员、Teacher 仅本人操作/本人授课课次；撤销限
/// Admin 或当日操作人本人）在 <see cref="ConsumptionService"/> 内裁决，
/// 400/403/404/409 经中间件映射。from/to 为可选的 yyyy-MM-dd（非法 400），
/// kind 为枚举名（安全解析，非法 400，绝不 500）。
/// </summary>
[ApiController]
[Route("api/org/consumptions")]
[Authorize(Policy = AuthPolicies.StaffOnly)]
public class OrgConsumptionsController(
    ConsumptionService consumptionService,
    ICurrentUser currentUser) : ControllerBase
{
    /// <summary>
    /// POST /api/org/consumptions/walk-in {studentId, sessionPackId} →
    /// 200 {id, remainingSessions}（核销后该包余量）。包须属于该学员
    /// （400）、Active（400）、有余量（400）；按包的 ProductId 走标准
    /// 扣减（同产品最早先扣），流水行不带课次 id。
    /// </summary>
    [HttpPost("walk-in")]
    [Authorize(Policy = AuthPolicies.AdminOrFrontDesk)]
    public async Task<ActionResult<WalkInResult>> WalkIn(WalkInRequest request) =>
        Ok(await consumptionService.WalkInAsync(request.StudentId, request.SessionPackId, currentUser));

    /// <summary>
    /// GET /api/org/consumptions?studentId&amp;from&amp;to&amp;kind&amp;page&amp;pageSize →
    /// Paged&lt;{id,studentId,studentName,productName,kind,direction,reason,
    /// operatorName,classSessionStartAt?,createdAt}&gt;（CreatedAt desc, Id desc）。
    /// 日期窗 from 含当日零点、to 排他次日零点。
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<Paged<ConsumptionItem>>> List(
        int? studentId, string? from, string? to, string? kind, int? page, int? pageSize)
    {
        var fromDate = ParseOptionalDate(from, "from");
        var toDate = ParseOptionalDate(to, "to");
        var (pageNumber, pageSizeValue) = Paging.Normalize(page, pageSize);
        return Ok(await consumptionService.ListAsync(
            studentId, fromDate, toDate, ParseKind(kind), pageNumber, pageSizeValue, currentUser));
    }

    /// <summary>
    /// POST /api/org/consumptions/{id}/reverse {reason} → 201 {id}（镜像行）。
    /// reason 必填、trim 后 ≤200 字（400）；目标须 Direction=+1（400）；
    /// 已撤销 409；Admin 任意，或本人操作且当天（否则 403）。
    /// </summary>
    [HttpPost("{id}/reverse")]
    public async Task<ActionResult<ReverseResult>> Reverse(int id, ReverseRequest request)
    {
        var mirror = await consumptionService.ReverseAsync(id, request.Reason, currentUser);
        return StatusCode(StatusCodes.Status201Created, new ReverseResult(mirror.Id));
    }

    /// <summary>
    /// 安全解析可选日期（严格 yyyy-MM-dd，本地日期）：缺失/null/空白 →
    /// null；非法值 → 400（BusinessRuleException），绝不 500。
    /// </summary>
    private static DateTime? ParseOptionalDate(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!DateTime.TryParseExact(
                value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            throw new BusinessRuleException($"{field} 必须是 yyyy-MM-dd 格式的日期");
        }

        return parsed.Date;
    }

    /// <summary>
    /// 安全解析 kind（区分大小写的枚举名 RollCall/WalkIn/Reversal）：缺失 →
    /// null（不过滤）；非法值（含未定义数字）→ 400，绝不 500。
    /// </summary>
    private static ConsumptionKind? ParseKind(string? kind)
    {
        if (string.IsNullOrWhiteSpace(kind))
        {
            return null;
        }

        if (!Enum.TryParse<ConsumptionKind>(kind, out var parsed) || !Enum.IsDefined(parsed))
        {
            throw new BusinessRuleException("kind 必须是 RollCall/WalkIn/Reversal 之一");
        }

        return parsed;
    }
}
