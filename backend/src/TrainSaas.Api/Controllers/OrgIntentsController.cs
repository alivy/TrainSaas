using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TrainSaas.Api.Auth;
using TrainSaas.Api.Domain;
using TrainSaas.Api.Dtos;
using TrainSaas.Api.Services;

namespace TrainSaas.Api.Controllers;

/// <summary>
/// 机构侧购买意向（线索）工作台。控制器整体挂 [AdminSalesFrontDesk]
/// （user_type=staff 且 role∈{Admin,Sales,FrontDesk}）——线索列表的读权限，
/// Teacher 一律 403。写路由（follow/mark-invalid/mark-converted）追加
/// [AdminOrSales]，两策略按 AND 组合：实际仅 Admin/Sales 可写，FrontDesk
/// 只读。数据面经租户过滤器天然限制在本机构，未知或跨机构的意向 id 一律
/// 404（不用 403 泄露存在性）。
/// </summary>
[ApiController]
[Route("api/org/intents")]
[Authorize(Policy = AuthPolicies.AdminSalesFrontDesk)]
public class OrgIntentsController(IntentService intentService, ICurrentUser currentUser)
    : ControllerBase
{
    /// <summary>
    /// GET /api/org/intents?status&amp;page&amp;pageSize →
    /// Paged&lt;{id,name,phone,productId,productName?,message,status,
    /// followedByStaffId,followedByName?,createdAt}&gt;。status 可选
    /// （New/Followed/Converted/Invalid，大小写不敏感，非法值 400）。
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<Paged<IntentListItem>>> List(
        string? status, int? page, int? pageSize) =>
        Ok(await intentService.ListAsync(status, page, pageSize));

    /// <summary>
    /// POST /api/org/intents/{id}/follow → 204。仅当 Status=New（认领）或
    /// FollowedByStaffId==自己（归属人再操作）时允许，否则 409；成功置
    /// Status=Followed + FollowedByStaffId=自己。
    /// </summary>
    [HttpPost("{id}/follow")]
    [Authorize(Policy = AuthPolicies.AdminOrSales)]
    public async Task<IActionResult> Follow(int id)
    {
        await intentService.FollowAsync(id, currentUser);
        return NoContent();
    }

    /// <summary>
    /// POST /api/org/intents/{id}/mark-converted {orderId?} → 204。可选的
    /// orderId 被接受但有意不持久化：PurchaseIntent 没有 OrderId 列（YAGNI，
    /// 订单闭环交付时再评估加列）。New 状态可直接转化。
    /// </summary>
    [HttpPost("{id}/mark-converted")]
    [Authorize(Policy = AuthPolicies.AdminOrSales)]
    public async Task<IActionResult> MarkConverted(int id, MarkConvertedRequest? request)
    {
        await intentService.SetStatusAsync(id, IntentStatus.Converted);
        return NoContent();
    }

    /// <summary>
    /// POST /api/org/intents/{id}/mark-invalid {reason?} → 204。reason 与
    /// orderId 同理：接受但不持久化（无对应列）。New 状态可直接作废。
    /// </summary>
    [HttpPost("{id}/mark-invalid")]
    [Authorize(Policy = AuthPolicies.AdminOrSales)]
    public async Task<IActionResult> MarkInvalid(int id, MarkInvalidRequest? request)
    {
        await intentService.SetStatusAsync(id, IntentStatus.Invalid);
        return NoContent();
    }
}
