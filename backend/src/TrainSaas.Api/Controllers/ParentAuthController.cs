using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using TrainSaas.Api.Auth;
using TrainSaas.Api.Data;
using TrainSaas.Api.Dtos;
using TrainSaas.Api.Errors;

namespace TrainSaas.Api.Controllers;

/// <summary>家长登录与改密。跨机构账号（ParentAccount 不受租户过滤器约束），签发 user_type=parent 的 JWT。</summary>
[ApiController]
[Route("api/parent")]
public class ParentAuthController(
    AppDbContext db,
    IPasswordHasherService hasher,
    TokenService tokens,
    ICurrentUser currentUser) : ControllerBase
{
    /// <summary>
    /// POST /api/parent/auth/login {phone,password} → {token,phone}。
    /// 手机号无账号与密码错误返回同一个 401 —— 不泄露账号存在性。
    /// I6：挂 "login" 固定窗口限流（默认 10 次/分钟/源 IP，超限 429）。
    /// </summary>
    [HttpPost("auth/login")]
    [EnableRateLimiting("login")]
    public async Task<ActionResult<ParentLoginResponse>> Login(ParentLoginRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Phone) || string.IsNullOrWhiteSpace(request.Password))
            return LoginFailed();

        var account = await db.ParentAccounts.SingleOrDefaultAsync(p => p.Phone == request.Phone);
        if (account is null || !hasher.Verify(account.PasswordHash, request.Password))
            return LoginFailed();

        return Ok(new ParentLoginResponse(tokens.IssueParentToken(account), account.Phone));
    }

    /// <summary>
    /// POST /api/parent/change-password {oldPassword,newPassword} [ParentOnly]。
    /// 新密码 ≥6 位（否则 400，BusinessRuleException）；旧密码错误 → 400。
    /// </summary>
    [HttpPost("change-password")]
    [Authorize(Policy = AuthPolicies.ParentOnly)]
    public async Task<IActionResult> ChangePassword(ParentChangePasswordRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.NewPassword) || request.NewPassword.Length < 6)
            throw new BusinessRuleException("新密码长度不得少于6位");

        if (string.IsNullOrWhiteSpace(request.OldPassword))
            throw new BusinessRuleException("旧密码错误");

        var phone = currentUser.Phone ?? throw new ForbiddenActionException("非家长身份");
        var account = await db.ParentAccounts.SingleOrDefaultAsync(p => p.Phone == phone)
            ?? throw new NotFoundException("家长账号不存在");

        if (!hasher.Verify(account.PasswordHash, request.OldPassword))
            throw new BusinessRuleException("旧密码错误");

        account.PasswordHash = hasher.Hash(request.NewPassword);
        await db.SaveChangesAsync();
        return NoContent();
    }

    private static UnauthorizedObjectResult LoginFailed() =>
        new(new ProblemDetails
        {
            Status = StatusCodes.Status401Unauthorized,
            Title = "Unauthorized",
            Detail = "手机号或密码错误",
        });
}
