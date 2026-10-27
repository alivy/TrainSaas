using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TrainSaas.Api.Auth;
using TrainSaas.Api.Domain;
using TrainSaas.Api.Dtos;
using TrainSaas.Api.Services;

namespace TrainSaas.Api.Controllers;

/// <summary>
/// 学员管理。读路由（列表/详情）挂 [StaffOnly]，角色范围（Admin/FrontDesk
/// 全机构；Sales 仅本人名下；Teacher 仅自己班里的学员；范围外一律 404）在
/// <see cref="StudentService"/> 内裁决。写路由（POST/PUT）追加
/// [AdminSalesFrontDesk]（user_type=staff 且 role∈{Admin,Sales,FrontDesk}），
/// 两策略按 AND 组合：Teacher 在写路由一律 403；PUT 的字段级权限再按
/// 角色在服务层细分（Sales 仅 gender/birthday/notes，FrontDesk 仅 notes）。
/// </summary>
[ApiController]
[Route("api/org/students")]
[Authorize(Policy = AuthPolicies.StaffOnly)]
public class OrgStudentsController(StudentService studentService, ICurrentUser currentUser) : ControllerBase
{
    /// <summary>
    /// GET /api/org/students?q&amp;phone&amp;page&amp;pageSize →
    /// Paged&lt;{id,name,phone,gender,birthday,assignedSalesId,assignedSalesName,
    /// isActive,createdAt}&gt;。q 为姓名子串（大小写不敏感）、phone 为精确
    /// 匹配，两者可选、可组合。
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<Paged<StudentListItem>>> Search(
        string? q, string? phone, int? page, int? pageSize)
    {
        var (pageNumber, pageSizeValue) = Paging.Normalize(page, pageSize);
        return Ok(await studentService.SearchAsync(q, phone, pageNumber, pageSizeValue, currentUser));
    }

    /// <summary>
    /// GET /api/org/students/{id} →
    /// {student, packs[], orders[], recentConsumptions[], enrollments[]}。
    /// 越权/跨租户/未知 id 一律 404。
    /// </summary>
    [HttpGet("{id}")]
    public async Task<ActionResult<StudentDetail>> GetDetail(int id) =>
        Ok(await studentService.GetDetailAsync(id, currentUser));

    /// <summary>
    /// POST /api/org/students {name,phone,gender?,birthday?,assignedSalesId?,
    /// notes?} → 201 {id,name,phone,gender,birthday,assignedSalesId,notes,
    /// isActive,createdAt}。Sales 创建时归属强制为自己；新学员默认激活。
    /// </summary>
    [HttpPost]
    [Authorize(Policy = AuthPolicies.AdminSalesFrontDesk)]
    public async Task<ActionResult<StudentResponse>> Create(CreateStudentRequest request)
    {
        var student = await studentService.CreateAsync(request, currentUser);
        return StatusCode(StatusCodes.Status201Created, ToResponse(student));
    }

    /// <summary>PUT /api/org/students/{id}——仅更新非 null 字段；字段级权限按角色。204。</summary>
    [HttpPut("{id}")]
    [Authorize(Policy = AuthPolicies.AdminSalesFrontDesk)]
    public async Task<IActionResult> Update(int id, UpdateStudentRequest request)
    {
        await studentService.UpdateAsync(id, request, currentUser);
        return NoContent();
    }

    private static StudentResponse ToResponse(Student student) =>
        new(student.Id, student.Name, student.Phone, student.Gender, student.Birthday,
            student.AssignedSalesId, student.Notes, student.IsActive, student.CreatedAt);
}
