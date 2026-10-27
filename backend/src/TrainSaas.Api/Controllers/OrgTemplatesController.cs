using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TrainSaas.Api.Auth;
using TrainSaas.Api.Domain;
using TrainSaas.Api.Dtos;
using TrainSaas.Api.Errors;
using TrainSaas.Api.Services;

namespace TrainSaas.Api.Controllers;

/// <summary>
/// 班级模板管理（Task 11 模板部分 + Task 12 报名部分）。控制器整体挂
/// [StaffOnly]（user_type=staff），模板的增改停启用与报班/退班再各自追加
/// [AdminOnly]（两策略按 AND 组合）：这些是机构管理动作。唯一放宽的是
/// GET /{id}/enrollments（班级名册）——Admin 任意、该模板授课教师可读自己
/// 班（裁决在服务层，其他人 403）。数据面经租户过滤器天然限制在本机构，
/// 未知或跨机构的模板/学员 id 一律 404；业务规则违反（非 Fixed 产品、教师
/// 角色不符、停用模板/学员、超容量、重复报名）一律 400/409
/// （<see cref="Errors.BusinessRuleException"/>/<see cref="Errors.ConflictException"/>）。
/// 模板修改只影响之后新生成的课次，已生成课次不变。
/// </summary>
[ApiController]
[Route("api/org/templates")]
[Authorize(Policy = AuthPolicies.StaffOnly)]
public class OrgTemplatesController(
    ClassTemplateService templateService,
    EnrollmentService enrollmentService,
    ICurrentUser currentUser) : ControllerBase
{
    /// <summary>
    /// GET /api/org/templates?productId&amp;page&amp;pageSize（AdminOnly）→
    /// Paged&lt;{id,productId,productName,dayOfWeek,startTime,endTime,
    /// teacherId,teacherName,room,capacity,isActive,enrolledCount}&gt;。
    /// productId 可选；enrolledCount 为在班（Enrolled）学员数。
    /// </summary>
    [HttpGet]
    [Authorize(Policy = AuthPolicies.AdminOnly)]
    public async Task<ActionResult<Paged<TemplateListItem>>> List(
        int? productId, int? page, int? pageSize) =>
        Ok(await templateService.ListAsync(productId, page, pageSize));

    /// <summary>
    /// POST /api/org/templates {productId,dayOfWeek,startTime"HH:mm",
    /// endTime,teacherId,room?,capacity?}（AdminOnly）→ 201 同字段响应。
    /// 新建模板默认激活；仅 Fixed 型产品可建班。
    /// </summary>
    [HttpPost]
    [Authorize(Policy = AuthPolicies.AdminOnly)]
    public async Task<ActionResult<TemplateResponse>> Create(CreateTemplateRequest request)
    {
        var template = await templateService.CreateAsync(request);
        return StatusCode(StatusCodes.Status201Created, ToResponse(template));
    }

    /// <summary>
    /// PUT /api/org/templates/{id}（AdminOnly）——仅更新非 null 字段，204；
    /// 未知 id 404。
    /// </summary>
    [HttpPut("{id}")]
    [Authorize(Policy = AuthPolicies.AdminOnly)]
    public async Task<IActionResult> Update(int id, UpdateTemplateRequest request)
    {
        await templateService.UpdateAsync(id, request);
        return NoContent();
    }

    /// <summary>
    /// POST /api/org/templates/{id}/deactivate（AdminOnly）→ 204。停用后
    /// 不再生成新课次，也不可再报班。
    /// </summary>
    [HttpPost("{id}/deactivate")]
    [Authorize(Policy = AuthPolicies.AdminOnly)]
    public async Task<IActionResult> Deactivate(int id) => await SetActiveAsync(id, active: false);

    /// <summary>POST /api/org/templates/{id}/activate（AdminOnly）→ 204。恢复生成。</summary>
    [HttpPost("{id}/activate")]
    [Authorize(Policy = AuthPolicies.AdminOnly)]
    public async Task<IActionResult> Activate(int id) => await SetActiveAsync(id, active: true);

    /// <summary>
    /// POST /api/org/templates/{id}/enrollments {studentId}（AdminOnly）→
    /// 201 {id}。模板须 IsActive、学员须本机构 IsActive；已在班 409；曾退班
    /// 复用原行翻回 Enrolled（同 id）；容量满 400 "班级已满"。
    /// </summary>
    [HttpPost("{id}/enrollments")]
    [Authorize(Policy = AuthPolicies.AdminOnly)]
    public async Task<ActionResult<EnrollmentCreatedResponse>> Enroll(int id, EnrollRequest request)
    {
        var enrollment = await enrollmentService.EnrollAsync(id, request.StudentId, RequireStaffId());
        return StatusCode(
            StatusCodes.Status201Created, new EnrollmentCreatedResponse(enrollment.Id));
    }

    /// <summary>
    /// DELETE /api/org/templates/{id}/enrollments/{studentId}（AdminOnly）→
    /// 204。置 Withdrawn（行保留为历史，重报复用）；无在班记录 404。
    /// </summary>
    [HttpDelete("{id}/enrollments/{studentId}")]
    [Authorize(Policy = AuthPolicies.AdminOnly)]
    public async Task<IActionResult> Withdraw(int id, int studentId)
    {
        await enrollmentService.WithdrawAsync(id, studentId);
        return NoContent();
    }

    /// <summary>
    /// GET /api/org/templates/{id}/enrollments →
    /// [{studentId,name,phone,status,enrolledAt}]（按 studentId 升序）。
    /// 仅 Enrolled 行（退班是历史，名册是当前班）。访问裁决在服务层：
    /// Admin 任意；该模板授课教师可读自己班；其他员工 403。
    /// </summary>
    [HttpGet("{id}/enrollments")]
    public async Task<ActionResult<List<RosterItem>>> Roster(int id) =>
        Ok(await enrollmentService.GetRosterAsync(id, currentUser));

    private async Task<IActionResult> SetActiveAsync(int id, bool active)
    {
        await templateService.SetActiveAsync(id, active);
        return NoContent();
    }

    /// <summary>防御性守卫：staff 令牌必含 staff_id，缺失即拒绝而非落脏数据。</summary>
    private int RequireStaffId() =>
        currentUser.StaffId ?? throw new ForbiddenActionException("员工令牌缺少 staff_id");

    private static TemplateResponse ToResponse(ClassTemplate template) =>
        new(template.Id, template.ProductId, (int)template.DayOfWeek, template.StartTime,
            template.EndTime, template.TeacherId, template.Room, template.Capacity, template.IsActive);
}
