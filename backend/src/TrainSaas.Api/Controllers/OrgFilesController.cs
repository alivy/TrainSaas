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
/// 机构侧文件上传（Task 9），整体 [StaffOnly]。
/// POST /api/org/files——通用图片（机构 Logo/产品封面，purpose ∈ {logo,cover}）
/// → 200 {path}（path 为相对存储路径，即落库原值）。
/// POST /api/org/files/vouchers/{orderId}——订单支付凭据：订单经租户过滤
/// 不可见（跨机构/未知）→ 404；Cancelled/Voided → 400；成功创建 Voucher 行
/// → 201 {id,url}。订单服务属 Task 10（裁定 R3），本任务直接查 DbContext。
/// </summary>
[ApiController]
[Route("api/org/files")]
[Authorize(Policy = AuthPolicies.StaffOnly)]
public class OrgFilesController(
    AppDbContext db,
    FileStorageService storage,
    ICurrentUser currentUser) : ControllerBase
{
    /// <summary>POST /api/org/files 允许的 purpose 集合（voucher 走专用路由）。</summary>
    private static readonly string[] GeneralPurposes = ["logo", "cover"];

    /// <summary>
    /// POST /api/org/files multipart(file, purpose∈{logo,cover}) → 200 {path}。
    /// 缺 file、非法 purpose、非白名单图片或超 5MB → 400。
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<UploadFileResponse>> Upload(
        [FromForm] IFormFile? file, [FromForm] string? purpose)
    {
        if (file is null)
        {
            throw new BusinessRuleException("缺少上传文件");
        }

        if (purpose is null || !GeneralPurposes.Contains(purpose))
        {
            throw new BusinessRuleException("上传用途无效，仅支持 logo、cover");
        }

        // 文件名/扩展名校验只发生在控制器（SaveImageAsync 签名不含文件名）。
        FileStorageService.ValidateImageFileName(file.FileName, file.ContentType);
        var stored = await SaveAsync(file, purpose);
        return Ok(new UploadFileResponse(stored.RelativePath));
    }

    /// <summary>
    /// POST /api/org/files/vouchers/{orderId} multipart(file) → 201 {id,url}。
    /// 订单不存在或跨机构 → 404（租户过滤器，两态同响应）；订单已取消/作废
    /// → 400；文件校验与 POST /api/org/files 相同。
    /// </summary>
    [HttpPost("vouchers/{orderId}")]
    public async Task<ActionResult<VoucherCreatedResponse>> UploadVoucher(
        int orderId, [FromForm] IFormFile? file)
    {
        // 裁定 R3：订单服务属 Task 10，这里直接经 DbContext 查询；
        // 租户过滤器使跨机构/未知订单同样表现为 404。
        var order = await db.Orders.SingleOrDefaultAsync(o => o.Id == orderId)
            ?? throw new NotFoundException("订单不存在");

        if (order.Status is OrderStatus.Cancelled or OrderStatus.Voided)
        {
            throw new BusinessRuleException("订单已取消或已作废，不能上传凭证");
        }

        if (file is null)
        {
            throw new BusinessRuleException("缺少上传文件");
        }

        FileStorageService.ValidateImageFileName(file.FileName, file.ContentType);
        var stored = await SaveAsync(file, "voucher");

        var voucher = new Voucher
        {
            OrderId = order.Id,
            FilePath = stored.RelativePath,
            UploadedById = RequireStaffId(),
            // 显式置当前机构（SaveChanges 的自动填充仅在 0 时介入）。
            InstitutionId = RequireInstitutionId(),
        };
        db.Vouchers.Add(voucher);
        await db.SaveChangesAsync();

        return StatusCode(
            StatusCodes.Status201Created,
            new VoucherCreatedResponse(voucher.Id, $"/api/files/vouchers/{voucher.Id}"));
    }

    private async Task<StoredFile> SaveAsync(IFormFile file, string purpose)
    {
        await using var stream = file.OpenReadStream();
        return await storage.SaveImageAsync(
            stream, file.ContentType, file.Length, RequireInstitutionId(), purpose);
    }

    /// <summary>防御性守卫：staff 令牌必含 institution_id/staff_id，缺失即拒绝而非落脏数据。</summary>
    private int RequireInstitutionId() =>
        currentUser.InstitutionId ?? throw new ForbiddenActionException("无租户上下文");

    private int RequireStaffId() =>
        currentUser.StaffId ?? throw new ForbiddenActionException("无员工上下文");
}
