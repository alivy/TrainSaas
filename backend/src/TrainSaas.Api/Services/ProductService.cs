using Microsoft.EntityFrameworkCore;
using TrainSaas.Api.Data;
using TrainSaas.Api.Domain;
using TrainSaas.Api.Dtos;
using TrainSaas.Api.Errors;

namespace TrainSaas.Api.Services;

/// <summary>
/// 课程产品管理服务。业务规则：name 必填且 ≤50 字；price ≥ 0；
/// sessionCount ≥ 1（Review Focus 3）；type ∈ Fixed/Flexible（大小写不敏感，
/// 安全解析）；新建默认未发布——发布/取消发布是显式动作。所有读写经租户
/// 过滤器限制在本机构：未知或跨机构的产品 id 一律 404。
/// </summary>
public class ProductService(AppDbContext db)
{
    private const int MaxNameLength = 50;

    /// <summary>
    /// GET /api/org/products?type&amp;published&amp;page&amp;pageSize 的数据源：本机构
    /// （租户过滤器）内按 Id 升序分页；type/published 可选，非法值 400。
    /// </summary>
    public async Task<Paged<ProductListItem>> ListAsync(
        string? type, string? published, int? page, int? pageSize)
    {
        var query = db.Products.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(type))
        {
            var parsed = ParseType(type);
            query = query.Where(p => p.Type == parsed);
        }

        if (!string.IsNullOrWhiteSpace(published))
        {
            if (!bool.TryParse(published, out var isPublished))
                throw new BusinessRuleException("published 参数无效");
            query = query.Where(p => p.IsPublished == isPublished);
        }

        var (pageNumber, pageSizeValue) = Paging.Normalize(page, pageSize);
        var total = await query.CountAsync();
        var products = await query
            .OrderBy(p => p.Id)
            .Skip((pageNumber - 1) * pageSizeValue)
            .Take(pageSizeValue)
            .ToListAsync();

        return new Paged<ProductListItem>(
            total,
            products.Select(ToListItem).ToList());
    }

    /// <summary>
    /// 创建产品 {name,type,price,sessionCount,description?}：默认未发布，
    /// 归属机构由 SaveChanges 按当前员工令牌自动填充。
    /// </summary>
    public async Task<Product> CreateAsync(CreateProductRequest request)
    {
        var product = new Product
        {
            Name = ValidateName(request.Name),
            Type = ParseType(request.Type),
            Price = ValidatePrice(request.Price),
            SessionCount = ValidateSessionCount(request.SessionCount),
            Description = request.Description,
            // 发布是显式动作：新产品一律从"未发布"开始。
            IsPublished = false,
            // InstitutionId 留 0：SaveChanges 按当前员工令牌的机构自动填充。
        };
        db.Products.Add(product);
        await db.SaveChangesAsync();
        return product;
    }

    /// <summary>
    /// PUT {name?,type?,price?,sessionCount?,description?,coverImagePath?}——
    /// 仅更新非 null 字段，非 null 值走与创建相同的校验。未知 id（含跨机构
    /// 不可见）→ 404。
    /// </summary>
    public async Task UpdateAsync(int id, UpdateProductRequest request)
    {
        var product = await FindProductOrThrowAsync(id);

        if (request.Name is not null)
            product.Name = ValidateName(request.Name);

        if (request.Type is not null)
            product.Type = ParseType(request.Type);

        if (request.Price is not null)
            product.Price = ValidatePrice(request.Price.Value);

        if (request.SessionCount is not null)
            product.SessionCount = ValidateSessionCount(request.SessionCount.Value);

        if (request.Description is not null)
            product.Description = request.Description;

        if (request.CoverImagePath is not null)
            product.CoverImagePath = request.CoverImagePath;

        await db.SaveChangesAsync();
    }

    /// <summary>发布/取消发布。未知 id（含跨机构不可见）→ 404。</summary>
    public async Task SetPublishedAsync(int id, bool published)
    {
        var product = await FindProductOrThrowAsync(id);
        product.IsPublished = published;
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// 安全解析课程类型：枚举名忽略大小写（"Fixed"）；数字串（"0"/"1"，
    /// 此前被 IsDefined 误收）与未定义名一律 400——统一走
    /// <see cref="QueryParsers.ParseEnum{T}"/>（M1 收敛，口径全站一致）。
    /// 非法值抛 <see cref="BusinessRuleException"/>（400），绝不 500。
    /// </summary>
    public static ProductType ParseType(string? type) =>
        QueryParsers.ParseEnum<ProductType>(type, "课程类型无效");

    private static string ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new BusinessRuleException("产品名称不能为空");
        if (name.Length > MaxNameLength)
            throw new BusinessRuleException($"产品名称不能超过 {MaxNameLength} 个字符");

        return name;
    }

    private static decimal ValidatePrice(decimal price)
    {
        if (price < 0)
            throw new BusinessRuleException("产品价格不能为负数");

        return price;
    }

    private static int ValidateSessionCount(int sessionCount)
    {
        if (sessionCount < 1)
            throw new BusinessRuleException("课时数必须至少为 1");

        return sessionCount;
    }

    /// <summary>按 id 查产品；租户过滤器下不可见（跨机构/不存在）→ 404。</summary>
    private async Task<Product> FindProductOrThrowAsync(int id) =>
        await db.Products.SingleOrDefaultAsync(p => p.Id == id)
            ?? throw new NotFoundException("产品不存在");

    private static ProductListItem ToListItem(Product product) =>
        new(product.Id, product.Name, product.Type.ToString(), product.Price, product.SessionCount,
            product.Description, product.CoverImagePath, product.IsPublished, product.CreatedAt);
}
