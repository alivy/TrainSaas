using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TrainSaas.Api.Auth;
using TrainSaas.Api.Data;
using TrainSaas.Api.Domain;
using TrainSaas.Api.Dtos;
using TrainSaas.Api.Errors;
using TrainSaas.Api.Services;

namespace TrainSaas.Api.Controllers;

/// <summary>
/// 平台超管的机构管理，整个控制器挂 [PlatformOnly]。Institution 不受租户
/// 过滤器约束，可直接查询；但对 Staff/Student 的计数与查询在 platform 令牌
/// （无租户上下文）下必须 <c>IgnoreQueryFilters()</c> + 显式
/// <c>InstitutionId ==</c> 条件。
/// </summary>
[ApiController]
[Route("api/platform/institutions")]
[Authorize(Policy = AuthPolicies.PlatformOnly)]
public class PlatformInstitutionsController(
    AppDbContext db,
    InstitutionProvisioningService provisioning,
    IPasswordHasherService hasher) : ControllerBase
{
    /// <summary>
    /// POST /api/platform/institutions → 201 {id,code,name}。编码格式非法 400；
    /// 编码重复或管理员手机号已被占用 409。
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<InstitutionCreatedResponse>> Create(CreateInstitutionRequest request)
    {
        var (institution, _) = await provisioning.CreateAsync(request);
        return StatusCode(StatusCodes.Status201Created,
            new InstitutionCreatedResponse(institution.Id, institution.Code, institution.Name));
    }

    /// <summary>
    /// GET /api/platform/institutions?status&amp;page&amp;pageSize →
    /// Paged&lt;{id,code,name,status,staffCount,studentCount,createdAt}&gt;。
    /// status 可选（Active/Disabled，枚举名忽略大小写；数字串与未定义名
    /// 一律 400，<see cref="QueryParsers"/> 统一口径）；计数用 CountAsync 配
    /// 显式 InstitutionId 谓词（int 计数，SQLite 聚合安全）。
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<Paged<InstitutionListItem>>> List(
        string? status, int? page, int? pageSize)
    {
        var query = db.Institutions.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(status))
        {
            var parsed = QueryParsers.ParseEnum<InstitutionStatus>(status, "机构状态参数无效");
            query = query.Where(i => i.Status == parsed);
        }

        var (pageNumber, pageSizeValue) = Paging.Normalize(page, pageSize);
        var total = await query.CountAsync();
        var institutions = await query
            .OrderBy(i => i.Id)
            .Skip((pageNumber - 1) * pageSizeValue)
            .Take(pageSizeValue)
            .ToListAsync();

        var items = new List<InstitutionListItem>(institutions.Count);
        foreach (var institution in institutions)
        {
            var staffCount = await db.Staff.IgnoreQueryFilters()
                .CountAsync(s => s.InstitutionId == institution.Id);
            var studentCount = await db.Students.IgnoreQueryFilters()
                .CountAsync(s => s.InstitutionId == institution.Id);
            items.Add(new InstitutionListItem(
                institution.Id,
                institution.Code,
                institution.Name,
                institution.Status.ToString(),
                staffCount,
                studentCount,
                institution.CreatedAt));
        }

        return Ok(new Paged<InstitutionListItem>(total, items));
    }

    /// <summary>
    /// PUT /api/platform/institutions/{id}——仅更新请求中非 null 的
    /// name/introText/contactPhone/address；机构不存在 404。
    /// </summary>
    [HttpPut("{id}")]
    public async Task<IActionResult> Update(int id, UpdateInstitutionRequest request)
    {
        var institution = await FindInstitutionOrThrowAsync(id);

        if (request.Name is not null)
            institution.Name = request.Name;
        if (request.IntroText is not null)
            institution.IntroText = request.IntroText;
        if (request.ContactPhone is not null)
            institution.ContactPhone = request.ContactPhone;
        if (request.Address is not null)
            institution.Address = request.Address;
        await db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>POST /api/platform/institutions/{id}/disable → 204；机构不存在 404。</summary>
    [HttpPost("{id}/disable")]
    public async Task<IActionResult> Disable(int id) =>
        await SetStatusAsync(id, InstitutionStatus.Disabled);

    /// <summary>POST /api/platform/institutions/{id}/enable → 204；机构不存在 404。</summary>
    [HttpPost("{id}/enable")]
    public async Task<IActionResult> Enable(int id) =>
        await SetStatusAsync(id, InstitutionStatus.Active);

    /// <summary>
    /// POST /api/platform/institutions/{id}/reset-admin-password → 204。把该机构
    /// 第一个 Role=Admin 且 IsActive 的员工（按 Id 升序）密码重置为初始密码；
    /// 机构不存在或无符合条件的管理员 → 404。
    /// </summary>
    [HttpPost("{id}/reset-admin-password")]
    public async Task<IActionResult> ResetAdminPassword(int id)
    {
        var institution = await FindInstitutionOrThrowAsync(id);

        // platform 令牌无租户上下文——必须忽略 Staff 的租户过滤器。
        var admin = await db.Staff.IgnoreQueryFilters()
            .Where(s => s.InstitutionId == institution.Id && s.Role == StaffRole.Admin && s.IsActive)
            .OrderBy(s => s.Id)
            .FirstOrDefaultAsync()
            ?? throw new NotFoundException("机构管理员不存在");

        admin.PasswordHash = hasher.Hash(DefaultPassword.Initial);
        await db.SaveChangesAsync();
        return NoContent();
    }

    private async Task<Institution> FindInstitutionOrThrowAsync(int id) =>
        await db.Institutions.SingleOrDefaultAsync(i => i.Id == id)
            ?? throw new NotFoundException("机构不存在");

    private async Task<IActionResult> SetStatusAsync(int id, InstitutionStatus status)
    {
        var institution = await FindInstitutionOrThrowAsync(id);
        institution.Status = status;
        await db.SaveChangesAsync();
        return NoContent();
    }
}
