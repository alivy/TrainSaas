using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TrainSaas.Api.Auth;
using TrainSaas.Api.Domain;
using TrainSaas.Api.Dtos;
using TrainSaas.Api.Errors;
using TrainSaas.Api.Services;

namespace TrainSaas.Api.Controllers;

/// <summary>
/// 订单管理（账务入口，Task 10）。读路由（列表/详情）挂 [StaffOnly]：
/// 全机构可见（无学员那样的角色缩小范围），数据面经租户过滤器限制在
/// 本机构，未知/跨机构 id 一律 404。写路由按动作收紧：
/// 创建追加 [AdminSalesFrontDesk]；确认/作废追加 [AdminOnly]（两策略按
/// AND 组合）；取消仅 [StaffOnly]——Admin 或订单创建人本人的裁决在服务层
/// （403）。状态机的全部业务规则与事务语义见 <see cref="OrderService"/>。
/// ?status= 过滤值经 <see cref="QueryParsers"/> 安全解析（枚举名忽略大小写，
/// 数字串与未定义名一律 400）。
/// </summary>
[ApiController]
[Route("api/org/orders")]
[Authorize(Policy = AuthPolicies.StaffOnly)]
public class OrgOrdersController(OrderService orderService, ICurrentUser currentUser) : ControllerBase
{
    /// <summary>
    /// GET /api/org/orders?status&amp;studentId&amp;page&amp;pageSize →
    /// Paged&lt;{id,orderNo,studentId,studentName,productId,productName,amount,
    /// salesId,salesName?,status,createdAt}&gt;。status/studentId 可选、可
    /// 组合；status 非法值安全解析为 400（绝不 500）。
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<Paged<OrderListItem>>> List(
        string? status, int? studentId, int? page, int? pageSize)
    {
        var parsedStatus = QueryParsers.ParseOptionalEnum<OrderStatus>(status, "订单状态无效");
        var (pageNumber, pageSizeValue) = Paging.Normalize(page, pageSize);
        return Ok(await orderService.ListAsync(parsedStatus, studentId, pageNumber, pageSizeValue));
    }

    /// <summary>
    /// GET /api/org/orders/{id} →
    /// {订单全字段, vouchers:[{id,url}], student:{id,name,phone},
    /// product:{id,name,type}, pack:{id,totalSessions,remainingSessions,
    /// status}?}。未知/跨机构 id 一律 404；pack 确认时才诞生。
    /// </summary>
    [HttpGet("{id}")]
    public async Task<ActionResult<OrderDetail>> GetDetail(int id) =>
        Ok(await orderService.GetDetailAsync(id));

    /// <summary>
    /// POST /api/org/orders {studentId, productId, amount, salesId?, remark?}
    /// → 201 {id, orderNo, status}。salesId 缺省=当前员工；amount ≥0
    /// （议价金额可与产品售价不同）。
    /// </summary>
    [HttpPost]
    [Authorize(Policy = AuthPolicies.AdminSalesFrontDesk)]
    public async Task<ActionResult<OrderCreatedResponse>> Create(CreateOrderRequest request)
    {
        var order = await orderService.CreateAsync(request);
        return StatusCode(
            StatusCodes.Status201Created,
            new OrderCreatedResponse(order.Id, order.OrderNo, order.Status.ToString()));
    }

    /// <summary>
    /// POST /api/org/orders/{id}/confirm（AdminOnly）→ 204。仅 Pending，
    /// 重复确认 409 "订单已处理"；单次 SaveChanges 落状态、课时包与家长账号。
    /// </summary>
    [HttpPost("{id}/confirm")]
    [Authorize(Policy = AuthPolicies.AdminOnly)]
    public async Task<IActionResult> Confirm(int id)
    {
        await orderService.ConfirmAsync(id, RequireStaffId());
        return NoContent();
    }

    /// <summary>
    /// POST /api/org/orders/{id}/cancel → 204。仅 Pending（否则 400）；
    /// 调用者须是 Admin 或订单创建人本人（否则 403）。
    /// </summary>
    [HttpPost("{id}/cancel")]
    public async Task<IActionResult> Cancel(int id)
    {
        await orderService.CancelAsync(id, RequireStaffId(), RequireRole());
        return NoContent();
    }

    /// <summary>
    /// POST /api/org/orders/{id}/void {reason, refundAmount?}（AdminOnly）
    /// → 204。仅 Active（Pending 引导走取消 400，终态 409）；原因必填
    /// ≤200；退款金额可空但 ≥0；课时包同批作废。
    /// </summary>
    [HttpPost("{id}/void")]
    [Authorize(Policy = AuthPolicies.AdminOnly)]
    public async Task<IActionResult> Void(int id, VoidOrderRequest request)
    {
        await orderService.VoidAsync(id, request.Reason, request.RefundAmount, RequireStaffId());
        return NoContent();
    }

    /// <summary>防御性守卫：staff 令牌必含 staff_id/role，缺失即拒绝而非落脏数据。</summary>
    private int RequireStaffId() =>
        currentUser.StaffId ?? throw new ForbiddenActionException("员工令牌缺少 staff_id");

    private StaffRole RequireRole() =>
        currentUser.Role ?? throw new ForbiddenActionException("员工令牌缺少 role");
}
