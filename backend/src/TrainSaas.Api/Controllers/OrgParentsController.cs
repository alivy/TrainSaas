using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TrainSaas.Api.Auth;
using TrainSaas.Api.Data;
using TrainSaas.Api.Dtos;
using TrainSaas.Api.Errors;
using TrainSaas.Api.Services;

namespace TrainSaas.Api.Controllers;

/// <summary>
/// 机构员工对家长账号的维护入口（规格 §6.2"忘记密码：联系机构重置"，最终
/// 审查 I2 补齐）。类级 [AdminSalesFrontDesk]（Teacher 403）。
///
/// 租户纪律：重置资格来自"调用者机构内存在该手机号的**在读**学员"——学员
/// 查询走租户过滤器天然限定本机构，绝不 IgnoreQueryFilters；ParentAccount
/// 本身是非租户实体（手机号全局唯一），普通按手机号查找即可。任何未命中
/// （隔壁机构的孩子、未知手机号、已停用学员、无家长账号）一律 404，同态
/// 响应不泄露家长账号或他机构学员的存在性。
/// </summary>
[ApiController]
[Route("api/org/parents")]
[Authorize(Policy = AuthPolicies.AdminSalesFrontDesk)]
public class OrgParentsController(AppDbContext db, IPasswordHasherService hasher) : ControllerBase
{
    /// <summary>
    /// POST /api/org/parents/reset-password {phone} → 204。phone 必填且
    /// <c>^1\d{10}$</c>（trim 后校验，非法 400）；本机构无该手机号的在读学员
    /// → 404；有学员但无家长账号 → 404；成功把 PasswordHash 重置为初始密码
    /// 123456（家长随后可登录并自助改密）。
    /// </summary>
    [HttpPost("reset-password")]
    public async Task<IActionResult> ResetPassword(ParentPasswordResetRequest request)
    {
        var phone = PhoneValidator.Validate(request.Phone, "手机号");

        // 资格闸：调用者机构内的在读学员（租户过滤器 = 员工令牌机构）。
        if (!await db.Students.AsNoTracking().AnyAsync(s => s.Phone == phone && s.IsActive))
            throw new NotFoundException("学员不存在");

        var account = await db.ParentAccounts.SingleOrDefaultAsync(p => p.Phone == phone)
            ?? throw new NotFoundException("家长账号不存在");

        account.PasswordHash = hasher.Hash(DefaultPassword.Initial);
        await db.SaveChangesAsync();
        return NoContent();
    }
}
