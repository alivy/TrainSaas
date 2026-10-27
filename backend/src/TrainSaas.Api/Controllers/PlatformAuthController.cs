using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TrainSaas.Api.Auth;
using TrainSaas.Api.Data;
using TrainSaas.Api.Dtos;

namespace TrainSaas.Api.Controllers;

/// <summary>
/// 平台超管登录。跨租户账号（PlatformUser 不受租户过滤器约束），登录成功签发
/// user_type=platform 的 JWT。
/// </summary>
[ApiController]
[Route("api/platform/auth")]
public class PlatformAuthController(AppDbContext db, IPasswordHasherService hasher, TokenService tokens)
    : ControllerBase
{
    /// <summary>POST /api/platform/auth/login {username,password} → {token,name}。登录失败统一 401。</summary>
    [HttpPost("login")]
    public async Task<ActionResult<PlatformLoginResponse>> Login(PlatformLoginRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
            return LoginFailed();

        var user = await db.PlatformUsers.SingleOrDefaultAsync(u => u.Username == request.Username);
        if (user is null || !hasher.Verify(user.PasswordHash, request.Password))
            return LoginFailed();

        return Ok(new PlatformLoginResponse(tokens.IssuePlatformToken(user), user.Username));
    }

    private static UnauthorizedObjectResult LoginFailed() =>
        new(new ProblemDetails
        {
            Status = StatusCodes.Status401Unauthorized,
            Title = "Unauthorized",
            Detail = "用户名或密码错误",
        });
}
