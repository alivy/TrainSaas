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
/// kind 为枚举名（忽略大小写；数字串与未定义名一律 400，
/// <see cref="QueryParsers"/> 全站统一口径）。
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
    /// （400）、Active（400）、有余量（400）；扣的就是所选包（R14，
    /// DeductFromPackAsync），流水行不带课次 id。
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
        var fromDate = QueryParsers.ParseOptionalDate(from, "from");
        var toDate = QueryParsers.ParseOptionalDate(to, "to");
        var parsedKind = QueryParsers.ParseOptionalEnum<ConsumptionKind>(
            kind, "kind 必须是 RollCall/WalkIn/Reversal 之一");
        var (pageNumber, pageSizeValue) = Paging.Normalize(page, pageSize);
        return Ok(await consumptionService.ListAsync(
            studentId, fromDate, toDate, parsedKind, pageNumber, pageSizeValue, currentUser));
    }

    /// <summary>
    /// POST /api/org/consumptions/{id}/reverse {reason} → 201 {id}（镜像行）。
    /// 权限先行（R14）：Admin 任意，或本人操作且当天（否则 403）；目标须
    /// Direction=+1（400）；reason 必填、trim 后 ≤200 字（400）；已撤销 409。
    /// </summary>
    [HttpPost("{id}/reverse")]
    public async Task<ActionResult<ReverseResult>> Reverse(int id, ReverseRequest request)
    {
        var mirror = await consumptionService.ReverseAsync(id, request.Reason, currentUser);
        return StatusCode(StatusCodes.Status201Created, new ReverseResult(mirror.Id));
    }
}
