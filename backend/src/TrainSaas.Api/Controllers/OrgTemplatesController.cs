using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TrainSaas.Api.Auth;
using TrainSaas.Api.Domain;
using TrainSaas.Api.Dtos;
using TrainSaas.Api.Services;

namespace TrainSaas.Api.Controllers;

/// <summary>
/// 班级模板管理（Task 11，仅模板部分——报班/退班是 Task 12）。控制器整体挂
/// [AdminOnly]（user_type=staff 且 role=Admin）：模板的增改停启用都是机构
/// 管理动作。数据面经租户过滤器天然限制在本机构，未知或跨机构的模板 id
/// 一律 404；业务规则违反（非 Fixed 产品、教师角色不符、时间格式等）一律
/// 400（<see cref="Errors.BusinessRuleException"/>）。模板修改只影响之后新生
/// 成的课次，已生成课次不变。
/// </summary>
[ApiController]
[Route("api/org/templates")]
[Authorize(Policy = AuthPolicies.AdminOnly)]
public class OrgTemplatesController(ClassTemplateService templateService) : ControllerBase
{
    /// <summary>
    /// GET /api/org/templates?productId&amp;page&amp;pageSize →
    /// Paged&lt;{id,productId,dayOfWeek,startTime,endTime,teacherId,room,
    /// capacity,isActive}&gt;。productId 可选。
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<Paged<TemplateResponse>>> List(
        int? productId, int? page, int? pageSize) =>
        Ok(await templateService.ListAsync(productId, page, pageSize));

    /// <summary>
    /// POST /api/org/templates {productId,dayOfWeek,startTime"HH:mm",
    /// endTime,teacherId,room?,capacity?} → 201 同字段响应。新建模板默认
    /// 激活；仅 Fixed 型产品可建班。
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<TemplateResponse>> Create(CreateTemplateRequest request)
    {
        var template = await templateService.CreateAsync(request);
        return StatusCode(StatusCodes.Status201Created, ToResponse(template));
    }

    /// <summary>PUT /api/org/templates/{id}——仅更新非 null 字段，204；未知 id 404。</summary>
    [HttpPut("{id}")]
    public async Task<IActionResult> Update(int id, UpdateTemplateRequest request)
    {
        await templateService.UpdateAsync(id, request);
        return NoContent();
    }

    /// <summary>POST /api/org/templates/{id}/deactivate → 204。停用后不再生成新课次。</summary>
    [HttpPost("{id}/deactivate")]
    public async Task<IActionResult> Deactivate(int id) => await SetActiveAsync(id, active: false);

    /// <summary>POST /api/org/templates/{id}/activate → 204。恢复生成。</summary>
    [HttpPost("{id}/activate")]
    public async Task<IActionResult> Activate(int id) => await SetActiveAsync(id, active: true);

    private async Task<IActionResult> SetActiveAsync(int id, bool active)
    {
        await templateService.SetActiveAsync(id, active);
        return NoContent();
    }

    private static TemplateResponse ToResponse(ClassTemplate template) =>
        new(template.Id, template.ProductId, (int)template.DayOfWeek, template.StartTime,
            template.EndTime, template.TeacherId, template.Room, template.Capacity, template.IsActive);
}
