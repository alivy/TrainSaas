namespace TrainSaas.Api.Dtos;

/// <summary>
/// POST /api/org/products {name,type,price,sessionCount,description?}。
/// type 以字符串传输（Fixed/Flexible），由 <see cref="Services.ProductService"/>
/// 安全解析，非法值抛 <see cref="Errors.BusinessRuleException"/>（400）。
/// </summary>
public record CreateProductRequest(
    string Name,
    string Type,
    decimal Price,
    int SessionCount,
    string? Description);

/// <summary>
/// POST /api/org/products → 201 {id,name,type,price,sessionCount,description,
/// coverImagePath,isPublished,createdAt}（新建默认未发布）。
/// </summary>
public record ProductCreatedResponse(
    int Id,
    string Name,
    string Type,
    decimal Price,
    int SessionCount,
    string? Description,
    string? CoverImagePath,
    bool IsPublished,
    DateTime CreatedAt);

/// <summary>PUT /api/org/products/{id}——仅更新非 null 字段。</summary>
public record UpdateProductRequest(
    string? Name,
    string? Type,
    decimal? Price,
    int? SessionCount,
    string? Description,
    string? CoverImagePath);

/// <summary>GET /api/org/products 列表项（type 为 "Fixed"/"Flexible"）。</summary>
public record ProductListItem(
    int Id,
    string Name,
    string Type,
    decimal Price,
    int SessionCount,
    string? Description,
    string? CoverImagePath,
    bool IsPublished,
    DateTime CreatedAt);
