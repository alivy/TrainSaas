using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using TrainSaas.Api.Auth;
using TrainSaas.Api.Data;
using TrainSaas.Api.Dtos;
using TrainSaas.Api.Domain;
using TrainSaas.Api.Errors;

namespace TrainSaas.Api.Controllers;

/// <summary>机构员工登录与当前身份。登录成功签发 user_type=staff 的 JWT（含机构/角色 claims）。</summary>
[ApiController]
[Route("api/org/auth")]
public class OrgAuthController(
    AppDbContext db,
    IPasswordHasherService hasher,
    TokenService tokens,
    ICurrentUser currentUser) : ControllerBase
{
    /// <summary>
    /// POST /api/org/auth/login {phone,password} →
    /// {token,name,role,institutionId,institutionName,institutionCode}。
    /// 登录失败统一 401；员工 IsActive=false → 403；所属机构 Disabled → 403（"机构已停用"）。
    /// I6：挂 "login" 固定窗口限流（默认 10 次/分钟/源 IP，超限 429）。
    /// </summary>
    [HttpPost("login")]
    [EnableRateLimiting("login")]
    public async Task<ActionResult<StaffLoginResponse>> Login(StaffLoginRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Phone) || string.IsNullOrWhiteSpace(request.Password))
            return LoginFailed();

        // 登录发生在任何租户上下文之前，而 Staff 受租户过滤器约束，
        // 因此这里忽略过滤器并按手机号显式查全局唯一的员工账号。
        var staff = await db.Staff.IgnoreQueryFilters().SingleOrDefaultAsync(s => s.Phone == request.Phone);
        if (staff is null || !hasher.Verify(staff.PasswordHash, request.Password))
            return LoginFailed();

        if (!staff.IsActive)
            throw new ForbiddenActionException("账号已停用");

        var institution = await db.Institutions.SingleAsync(i => i.Id == staff.InstitutionId);
        if (institution.Status == InstitutionStatus.Disabled)
            throw new ForbiddenActionException("机构已停用");

        return Ok(new StaffLoginResponse(
            tokens.IssueStaffToken(staff, institution),
            staff.Name,
            staff.Role.ToString(),
            institution.Id,
            institution.Name,
            institution.Code));
    }

    /// <summary>
    /// GET /api/org/auth/me [StaffOnly] →
    /// {staffId,name,role,institutionId,institutionName,institutionCode}。
    /// staffId/institutionId 取自 token claims（字面 claim 名），员工行经租户过滤器读取，
    /// 机构按 claim 的 institution_id 从 DbContext 读取。
    /// </summary>
    [HttpGet("me")]
    [Authorize(Policy = AuthPolicies.StaffOnly)]
    public async Task<ActionResult<StaffMeResponse>> Me()
    {
        if (currentUser.StaffId is not int staffId || currentUser.InstitutionId is not int institutionId)
            throw new ForbiddenActionException("非员工身份");

        var staff = await db.Staff.SingleOrDefaultAsync(s => s.Id == staffId)
            ?? throw new NotFoundException("员工不存在");

        var institution = await db.Institutions.FindAsync(institutionId)
            ?? throw new NotFoundException("机构不存在");

        return Ok(new StaffMeResponse(
            staff.Id,
            staff.Name,
            staff.Role.ToString(),
            institution.Id,
            institution.Name,
            institution.Code));
    }

    private static UnauthorizedObjectResult LoginFailed() =>
        new(new ProblemDetails
        {
            Status = StatusCodes.Status401Unauthorized,
            Title = "Unauthorized",
            Detail = "手机号或密码错误",
        });
}
