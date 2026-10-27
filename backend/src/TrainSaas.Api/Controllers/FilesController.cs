using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TrainSaas.Api.Auth;
using TrainSaas.Api.Data;
using TrainSaas.Api.Errors;
using TrainSaas.Api.Services;

namespace TrainSaas.Api.Controllers;

/// <summary>
/// 文件下发（Task 9 + 最终审查 I3）。两条路由、两种信任级别：
/// <list type="bullet">
/// <item>GET vouchers/{id}——授权下发（spec 技术决策：凭据不做匿名静态访问）：
/// 必须携带对应机构的 staff token；跨机构/未知 id 经租户过滤器一律 404。</item>
/// <item>GET images/{**relativePath}——匿名下发（I3）：机构主页/产品页要用
/// &lt;img&gt; 直接引用 logo 与封面，无令牌可用。前缀白名单只放行 logo/ 与
/// cover/（大小写敏感、仅正斜杠）；voucher/ 绝不匿名下发（文件物理存在也
/// 404）。</item>
/// </list>
/// 两条路由的文件路径都只取数据库存储值或服务端校验过的相对路径，下发前经
/// <see cref="FileStorageService.ResolveForServing"/> 锚回上传根并做穿越防线
/// 断言 + 物理存在 + 扩展名白名单，任何失败一律 404（不区分原因）。
/// </summary>
[ApiController]
[Route("api/files")]
[Authorize(Policy = AuthPolicies.StaffOnly)]
public class FilesController(AppDbContext db, FileStorageService storage) : ControllerBase
{
    /// <summary>匿名图片路由放行的相对路径前缀（大小写敏感，正斜杠）。</summary>
    private static readonly string[] PublicImagePrefixes = ["logo/", "cover/"];

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

    /// <summary>
    /// GET /api/files/images/{**relativePath}（匿名，I3）：logo/cover 图片
    /// 下发。前缀白名单先行（非 logo/、cover/ 开头一律 404——voucher/ 凭据
    /// 因此绝不匿名可达），再过 ResolveForServing 的穿越防线（相对路径按
    /// URL 解码后可含 ../，锚回上传根后断言仍在根内）、物理存在与扩展名
    /// 白名单；任何失败同态 404，不泄露原因。
    /// </summary>
    [HttpGet("images/{**relativePath}")]
    [AllowAnonymous]
    public IActionResult GetImage(string? relativePath)
    {
        if (relativePath is null
            || !PublicImagePrefixes.Any(prefix =>
                relativePath.StartsWith(prefix, StringComparison.Ordinal)))
        {
            throw new NotFoundException("图片不存在");
        }

        var stored = storage.ResolveForServing(relativePath);
        return PhysicalFile(stored.AbsolutePath, stored.ContentType);
    }
}
