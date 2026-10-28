using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using TrainSaas.Api.Auth;
using TrainSaas.Api.Data;
using TrainSaas.Api.Domain;
using TrainSaas.Api.Dtos;
using TrainSaas.Api.Errors;

namespace TrainSaas.Api.Controllers;

/// <summary>
/// 平台超管登录与改密。跨租户账号（PlatformUser 不受租户过滤器约束），登录
/// 成功签发 user_type=platform 的 JWT。
/// </summary>
[ApiController]
[Route("api/platform/auth")]
public class PlatformAuthController(
    AppDbContext db,
    IPasswordHasherService hasher,
    TokenService tokens,
    ICurrentUser currentUser) : ControllerBase
{
    /// <summary>
    /// POST /api/platform/auth/login {username,password} → {token,name}。登录失败统一 401。
    /// I6：挂 "login" 固定窗口限流（默认 10 次/分钟/源 IP，超限 429）。
    /// </summary>
    [HttpPost("login")]
    [EnableRateLimiting("login")]
    public async Task<ActionResult<PlatformLoginResponse>> Login(PlatformLoginRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
            return LoginFailed();

        var user = await db.PlatformUsers.SingleOrDefaultAsync(u => u.Username == request.Username);
        if (user is null || !hasher.Verify(user.PasswordHash, request.Password))
            return LoginFailed();

        return Ok(new PlatformLoginResponse(tokens.IssuePlatformToken(user), user.Username));
    }

    /// <summary>
    /// POST /api/platform/auth/change-password {oldPassword,newPassword}
    /// [PlatformOnly]（I5：生产引导闭环——初始密码 123456 的平台超管第一时间
    /// 改掉它，见 README「生产首次部署」）。新密码必填且 ≥6 位，否则 400；
    /// 旧密码错误 → 401（与登录失败同码，改密接口先验证旧凭据再放行）；
    /// 成功 → 204，旧密码立即失效。账号按令牌 name claim（= Username）定位，
    /// 平台令牌由登录签发、name 即用户名。
    /// </summary>
    [HttpPost("change-password")]
    [Authorize(Policy = AuthPolicies.PlatformOnly)]
    public async Task<IActionResult> ChangePassword(PlatformChangePasswordRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.NewPassword) || request.NewPassword.Length < 6)
            throw new BusinessRuleException("新密码长度不得少于6位");

        if (string.IsNullOrWhiteSpace(request.OldPassword))
            return WrongOldPassword();

        var username = currentUser.UserType == UserType.Platform ? currentUser.Name : null;
        if (string.IsNullOrEmpty(username))
            throw new ForbiddenActionException("非平台超管身份");

        var user = await db.PlatformUsers.SingleOrDefaultAsync(u => u.Username == username);
        if (user is null || !hasher.Verify(user.PasswordHash, request.OldPassword))
            return WrongOldPassword();

        user.PasswordHash = hasher.Hash(request.NewPassword);
        await db.SaveChangesAsync();
        return NoContent();
    }

    private static UnauthorizedObjectResult LoginFailed() =>
        new(new ProblemDetails
        {
            Status = StatusCodes.Status401Unauthorized,
            Title = "Unauthorized",
            Detail = "用户名或密码错误",
        });

    private static UnauthorizedObjectResult WrongOldPassword() =>
        new(new ProblemDetails
        {
            Status = StatusCodes.Status401Unauthorized,
            Title = "Unauthorized",
            Detail = "旧密码错误",
        });
}
