using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TrainSaas.Api.Auth;
using TrainSaas.Api.Domain;
using TrainSaas.Api.Dtos;
using TrainSaas.Api.Errors;
using TrainSaas.Api.Services;

namespace TrainSaas.Api.Controllers;

/// <summary>
/// 机构员工管理，整个控制器挂 [AdminOnly]（user_type=staff 且 role=Admin）。
/// 数据面经租户过滤器天然限制在本机构；全局手机号唯一性检查在
/// <see cref="StaffService"/> 内 IgnoreQueryFilters 完成。
/// </summary>
[ApiController]
[Route("api/org/staff")]
[Authorize(Policy = AuthPolicies.AdminOnly)]
public class OrgStaffController(StaffService staffService, ICurrentUser currentUser) : ControllerBase
{
    /// <summary>
    /// GET /api/org/staff?role&amp;page&amp;pageSize →
    /// Paged&lt;{id,name,phone,role,isActive,createdAt}&gt;。role 可选
    /// （Admin/Sales/Teacher/FrontDesk，非法值 400）。
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<Paged<StaffListItem>>> List(string? role, int? page, int? pageSize) =>
        Ok(await staffService.ListAsync(role, page, pageSize));

    /// <summary>
    /// POST /api/org/staff {name,phone,role} → 201 {id,name,phone,role,isActive}。
    /// 初始密码 123456、默认激活、归属本机构；phone 被任意机构占用 → 409。
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<StaffCreatedResponse>> Create(CreateStaffRequest request)
    {
        var staff = await staffService.CreateAsync(request);
        return StatusCode(StatusCodes.Status201Created, ToCreatedResponse(staff));
    }

    /// <summary>
    /// PUT /api/org/staff/{id} {name?,role?}——仅更新非 null 字段；未知 id 404。
    /// 角色改写以调用者的**数据库状态**为准（I1：陈旧令牌窗口内被降级/停用者
    /// 403，守卫在 StaffService）。
    /// </summary>
    [HttpPut("{id}")]
    public async Task<IActionResult> Update(int id, UpdateStaffRequest request)
    {
        await staffService.UpdateAsync(id, request, RequireStaffId());
        return NoContent();
    }

    /// <summary>POST /api/org/staff/{id}/deactivate → 204；停用自己 403；最后一个活跃 Admin 409。</summary>
    [HttpPost("{id}/deactivate")]
    public async Task<IActionResult> Deactivate(int id) =>
        await SetActiveAsync(id, active: false);

    /// <summary>POST /api/org/staff/{id}/activate → 204。</summary>
    [HttpPost("{id}/activate")]
    public async Task<IActionResult> Activate(int id) =>
        await SetActiveAsync(id, active: true);

    /// <summary>POST /api/org/staff/{id}/reset-password → 204（重置为初始密码 123456）。</summary>
    [HttpPost("{id}/reset-password")]
    public async Task<IActionResult> ResetPassword(int id)
    {
        await staffService.ResetPasswordAsync(id);
        return NoContent();
    }

    private async Task<IActionResult> SetActiveAsync(int id, bool active)
    {
        await staffService.SetActiveAsync(id, active, RequireStaffId());
        return NoContent();
    }

    /// <summary>防御性守卫：staff 令牌必含 staff_id，缺失即拒绝而非落脏数据。</summary>
    private int RequireStaffId() =>
        currentUser.StaffId ?? throw new ForbiddenActionException("非员工身份");

    private static StaffCreatedResponse ToCreatedResponse(Staff staff) =>
        new(staff.Id, staff.Name, staff.Phone, staff.Role.ToString(), staff.IsActive);
}
