using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TrainSaas.Api.Auth;
using TrainSaas.Api.Data;
using TrainSaas.Api.Domain;
using TrainSaas.Api.Dtos;
using TrainSaas.Api.Errors;

namespace TrainSaas.Api.Controllers;

/// <summary>
/// 机构页设置（Task 15）：机构读写自己的公开介绍页内容。GET 挂 [StaffOnly]
/// （全体员工可见自家资料），PUT 挂 [AdminOnly]。
///
/// 租户过滤豁免纪律：Institution 不是 ITenanted（它本身就是租户），没有全局
/// 过滤器可依仗，因此这里按公开页/家长端同一套纪律显式收口——
/// <c>IgnoreQueryFilters()</c> 之后紧跟显式 <c>Id == 令牌机构</c> 谓词，
/// 绝不裸查表（否则任何机构的令牌都能读到/改到别人家的机构页）。
/// </summary>
[ApiController]
[Route("api/org/profile")]
public class OrgProfileController(AppDbContext db, ICurrentUser currentUser) : ControllerBase
{
    /// <summary>机构名称长度上限（与学员/产品名称同规则）。</summary>
    private const int MaxNameLength = 50;

    /// <summary>
    /// GET /api/org/profile → {code,name,logoPath,introText,contactPhone,address}。
    /// code 只读（改编码是平台侧的事）。
    /// </summary>
    [HttpGet]
    [Authorize(Policy = AuthPolicies.StaffOnly)]
    public async Task<ActionResult<OrgProfileResponse>> Get()
    {
        var institution = await OwnInstitution().AsNoTracking().SingleOrDefaultAsync()
            ?? throw new NotFoundException("机构不存在");

        return Ok(new OrgProfileResponse(
            institution.Code,
            institution.Name,
            institution.LogoPath,
            institution.IntroText,
            institution.ContactPhone,
            institution.Address));
    }

    /// <summary>
    /// PUT /api/org/profile [AdminOnly] {name?,logoPath?,introText?,contactPhone?,
    /// address?} → 204。仅更新请求中非 null 的字段；name 若提供则必填非空且 ≤50 字
    /// （否则 400）。改动立刻反映在匿名公开页 GET /api/public/orgs/{code} 上。
    /// </summary>
    [HttpPut]
    [Authorize(Policy = AuthPolicies.AdminOnly)]
    public async Task<IActionResult> Update(UpdateOrgProfileRequest request)
    {
        var institution = await OwnInstitution().SingleOrDefaultAsync()
            ?? throw new NotFoundException("机构不存在");

        if (request.Name is not null)
            institution.Name = ValidateName(request.Name);
        if (request.LogoPath is not null)
            institution.LogoPath = request.LogoPath;
        if (request.IntroText is not null)
            institution.IntroText = request.IntroText;
        if (request.ContactPhone is not null)
            institution.ContactPhone = request.ContactPhone;
        if (request.Address is not null)
            institution.Address = request.Address;

        await db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>
    /// 本机构查询（豁免纪律）：IgnoreQueryFilters 紧跟显式 <c>Id == 令牌机构</c>。
    /// 令牌缺 institution_id 视为损坏令牌（403）。
    /// </summary>
    private IQueryable<Institution> OwnInstitution()
    {
        var institutionId = currentUser.InstitutionId
            ?? throw new ForbiddenActionException("员工令牌缺少 institution_id");
        return db.Institutions.IgnoreQueryFilters().Where(i => i.Id == institutionId);
    }

    private static string ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new BusinessRuleException("机构名称不能为空");
        if (name.Length > MaxNameLength)
            throw new BusinessRuleException($"机构名称不能超过 {MaxNameLength} 个字符");

        return name;
    }
}
