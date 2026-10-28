using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using TrainSaas.Api.Auth;
using TrainSaas.Api.Data;
using TrainSaas.Api.Domain;
using TrainSaas.Api.Dtos;
using TrainSaas.Api.Errors;

namespace TrainSaas.Api.Services;

/// <summary>
/// 公开介绍页与购买意向（线索）服务。
///
/// 租户过滤的关键约束：公开端点（介绍页/意向提交）是匿名请求，
/// ICurrentUser.InstitutionId 为 null，租户过滤器会把 Product/PurchaseIntent
/// 的查询折叠成空集。因此公开路径上所有 ITenanted 实体的读写一律
/// IgnoreQueryFilters() + 显式 InstitutionId == institution.Id 谓词（继家长端
/// 之后的第二个设计内豁免）；写 PurchaseIntent 时显式落 InstitutionId
/// （SaveChanges 的自动填充在无租户上下文时会抛 403）。Institution 本身不受
/// 租户过滤，但查询仍统一 IgnoreQueryFilters + 显式 Code== 条件以保持纪律。
///
/// Org 端（列表/跟进/转化）在员工令牌下运行，天然租户过滤，未知或跨机构
/// 的意向 id 一律 404。
/// </summary>
public class IntentService(AppDbContext db)
{
    private const int MaxMessageLength = 200;

    /// <summary>意向人手机号：1 开头的 11 位数字（与学员手机号同规则）。</summary>
    public static readonly Regex PhonePattern = new(@"^1\d{10}$", RegexOptions.Compiled);

    /// <summary>
    /// GET /api/public/orgs/{code}：机构不存在或 Status!=Active → 404（两者
    /// 响应一致，不向匿名访客泄露存在性）；仅返回该机构 IsPublished 的产品，
    /// 按 Id 升序。
    /// </summary>
    public async Task<PublicOrgResponse> GetPublicOrgAsync(string code)
    {
        var institution = await FindActiveInstitutionAsync(code);

        // 匿名无租户上下文：必须绕过租户过滤器并显式限定机构。
        var products = await db.Products
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(p => p.InstitutionId == institution.Id && p.IsPublished)
            .OrderBy(p => p.Id)
            .ToListAsync();

        return new PublicOrgResponse(
            institution.Code,
            institution.Name,
            institution.LogoPath,
            institution.IntroText,
            institution.ContactPhone,
            institution.Address,
            products.Select(p => new PublicOrgProductItem(
                p.Id,
                p.Name,
                p.Type.ToString(),
                p.Price,
                p.SessionCount,
                p.Description,
                p.CoverImagePath)).ToList());
    }

    /// <summary>
    /// POST /api/public/orgs/{code}/intents：phone 必填且 <c>^1\d{10}$</c>；
    /// productId（若提供）必须是本机构已发布产品；message ≤200；非法值 400。
    /// 成功落库 Status=New（路由再挂 5 次/分钟/IP 的固定窗口限流）。
    /// </summary>
    public async Task<PurchaseIntent> CreateIntentAsync(string code, CreateIntentRequest request)
    {
        var institution = await FindActiveInstitutionAsync(code);
        var phone = ValidatePhone(request.Phone);
        var message = ValidateMessage(request.Message);

        if (request.ProductId is int productId)
        {
            // 匿名无租户上下文：显式限定"本机构且已发布"，跨机构/未发布一律 400。
            var isPublishedHere = await db.Products
                .IgnoreQueryFilters()
                .AsNoTracking()
                .AnyAsync(p => p.Id == productId
                    && p.InstitutionId == institution.Id
                    && p.IsPublished);
            if (!isPublishedHere)
                throw new BusinessRuleException("产品不存在或未发布");
        }

        var intent = new PurchaseIntent
        {
            // 匿名写入无租户上下文：显式归属本机构，避免依赖 SaveChanges 的
            // 自动填充（其在此场景会抛 ForbiddenActionException）。
            InstitutionId = institution.Id,
            Name = request.Name,
            Phone = phone,
            ProductId = request.ProductId,
            Message = message,
            Status = IntentStatus.New,
        };
        db.PurchaseIntents.Add(intent);
        await db.SaveChangesAsync();
        return intent;
    }

    /// <summary>
    /// GET /api/org/intents?status&amp;page&amp;pageSize 的数据源：本机构（租户
    /// 过滤器）内按 Id 升序分页；status 可选（New/Followed/Converted/Invalid，
    /// 大小写不敏感安全解析，非法值 400）。productName/followedByName 在内存中
    /// 批量解析，缺失为 null。
    /// </summary>
    public async Task<Paged<IntentListItem>> ListAsync(string? status, int? page, int? pageSize)
    {
        var query = db.PurchaseIntents.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(status))
        {
            var parsed = ParseStatus(status);
            query = query.Where(i => i.Status == parsed);
        }

        var (pageNumber, pageSizeValue) = Paging.Normalize(page, pageSize);
        var total = await query.CountAsync();
        var intents = await query
            .OrderBy(i => i.Id)
            .Skip((pageNumber - 1) * pageSizeValue)
            .Take(pageSizeValue)
            .ToListAsync();

        // 名称解析（产品/员工）在内存中做，避免多表 join 的翻译差异。
        var productIds = intents
            .Select(i => i.ProductId)
            .Where(id => id is not null)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();
        var productNames = productIds.Count == 0
            ? new Dictionary<int, string>()
            : await db.Products.AsNoTracking()
                .Where(p => productIds.Contains(p.Id))
                .ToDictionaryAsync(p => p.Id, p => p.Name);

        var staffIds = intents
            .Select(i => i.FollowedByStaffId)
            .Where(id => id is not null)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();
        var staffNames = staffIds.Count == 0
            ? new Dictionary<int, string>()
            : await db.Staff.AsNoTracking()
                .Where(s => staffIds.Contains(s.Id))
                .ToDictionaryAsync(s => s.Id, s => s.Name);

        return new Paged<IntentListItem>(total, intents.Select(i => new IntentListItem(
            i.Id,
            i.Name,
            i.Phone,
            i.ProductId,
            i.ProductId is int productId ? productNames.GetValueOrDefault(productId) : null,
            i.Message,
            i.Status.ToString(),
            i.FollowedByStaffId,
            i.FollowedByStaffId is int staffId ? staffNames.GetValueOrDefault(staffId) : null,
            i.CreatedAt)).ToList());
    }

    /// <summary>
    /// POST /api/org/intents/{id}/follow [AdminOrSales]：仅当 Status==New 或
    /// FollowedByStaffId==自己时允许（New 允许任何人认领，已跟进的线索只有
    /// 归属人可再操作），否则 409 ConflictException。成功置 Status=Followed +
    /// FollowedByStaffId=自己。未知/跨机构 id → 404。
    /// </summary>
    public async Task FollowAsync(int id, ICurrentUser caller)
    {
        var staffId = caller.StaffId ?? throw new ForbiddenActionException("员工令牌缺少 staff_id");

        // 租户过滤器下的按 id 查找：跨机构/不存在一律 404。
        var intent = await db.PurchaseIntents
            .SingleOrDefaultAsync(i => i.Id == id)
            ?? throw new NotFoundException("购买意向不存在");

        if (intent.Status != IntentStatus.New && intent.FollowedByStaffId != staffId)
            throw new ConflictException("该意向已由其他销售跟进");

        intent.Status = IntentStatus.Followed;
        intent.FollowedByStaffId = staffId;
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// POST /api/org/intents/{id}/mark-converted、mark-invalid [AdminOrSales]：
    /// 仅置状态（Converted/Invalid），无状态前置条件——New 可直接转化/作废。
    /// 控制器接收的 {orderId?}/{reason?} 有意不持久化（PurchaseIntent 无对应
    /// 列，YAGNI）。未知/跨机构 id → 404。
    /// </summary>
    public async Task SetStatusAsync(int id, IntentStatus status)
    {
        var intent = await db.PurchaseIntents
            .SingleOrDefaultAsync(i => i.Id == id)
            ?? throw new NotFoundException("购买意向不存在");

        intent.Status = status;
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// 安全解析意向状态：枚举名忽略大小写（"New"）；数字串（"0"…"3"，
    /// 此前被 IsDefined 误收）与未定义名一律 400——统一走
    /// <see cref="QueryParsers.ParseEnum{T}"/>（M1 收敛，口径全站一致）。
    /// 非法值抛 <see cref="BusinessRuleException"/>（400），绝不 500。
    /// </summary>
    public static IntentStatus ParseStatus(string? status) =>
        QueryParsers.ParseEnum<IntentStatus>(status, "状态参数无效");

    /// <summary>
    /// 公开端点的机构定位：不存在或非 Active 一律 404（响应一致，不泄露
    /// 存在性）；显式 Code== 条件。
    /// </summary>
    private async Task<Institution> FindActiveInstitutionAsync(string code) =>
        await db.Institutions
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleOrDefaultAsync(i => i.Code == code && i.Status == InstitutionStatus.Active)
            ?? throw new NotFoundException("机构不存在");

    private static string ValidatePhone(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone))
            throw new BusinessRuleException("意向人手机号不能为空");
        if (!PhonePattern.IsMatch(phone))
            throw new BusinessRuleException("意向人手机号格式无效");

        return phone;
    }

    private static string? ValidateMessage(string? message)
    {
        if (message is not null && message.Length > MaxMessageLength)
            throw new BusinessRuleException($"留言不能超过 {MaxMessageLength} 个字符");

        return message;
    }
}
