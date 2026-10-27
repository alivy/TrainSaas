using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TrainSaas.Api.Auth;
using TrainSaas.Api.Data;
using TrainSaas.Api.Errors;
using TrainSaas.Api.Services;

namespace TrainSaas.Api.Controllers;

/// <summary>
/// 授权文件下发（Task 9）。凭据不做匿名静态访问（spec 技术决策）：必须
/// 携带对应机构的 staff token；跨机构/未知 id 经租户过滤器一律 404。文件
/// 路径只取数据库存储的相对路径（服务端生成），下发前锚回上传根并做
/// 穿越防线断言（见 <see cref="FileStorageService.ResolveForServing"/>）。
/// </summary>
[ApiController]
[Route("api/files")]
[Authorize(Policy = AuthPolicies.StaffOnly)]
public class FilesController(AppDbContext db, FileStorageService storage) : ControllerBase
{
    /// <summary>
    /// GET /api/files/vouchers/{id}：租户过滤后的 Voucher 查行 → 相对路径
    /// 解析（根内断言 + 物理存在 + 扩展名推断 content type）→ PhysicalFile
    /// 下发。行不可见或文件缺失 → 404；匿名 → 401（策略层）。
    /// </summary>
    [HttpGet("vouchers/{id}")]
    public async Task<IActionResult> GetVoucher(int id)
    {
        var voucher = await db.Vouchers.SingleOrDefaultAsync(v => v.Id == id)
            ?? throw new NotFoundException("凭证不存在");

        var stored = storage.ResolveForServing(voucher.FilePath);
        return PhysicalFile(stored.AbsolutePath, stored.ContentType);
    }
}
