namespace TrainSaas.Api.Dtos;

/// <summary>
/// POST /api/public/orgs/{code}/intents {name?,phone,productId?,message?}。
/// phone 必填且 <c>^1\d{10}$</c>；productId（若提供）必须是该机构已发布产品；
/// message ≤200；非法值一律 400（BusinessRuleException）。
/// </summary>
public record CreateIntentRequest(
    string? Name,
    string Phone,
    int? ProductId,
    string? Message);

/// <summary>POST /api/public/orgs/{code}/intents → 201 {id}（Status=New）。</summary>
public record IntentCreatedResponse(int Id);

/// <summary>
/// GET /api/public/orgs/{code} 响应：机构公开信息 + 已发布产品（按 Id 升序）。
/// 机构不存在或 Disabled → 404。
/// </summary>
public record PublicOrgResponse(
    string Code,
    string Name,
    string? LogoPath,
    string? IntroText,
    string? ContactPhone,
    string? Address,
    List<PublicOrgProductItem> Products);

/// <summary>公开介绍页中的产品项：{id,name,type,price,sessionCount,description,coverImagePath}。</summary>
public record PublicOrgProductItem(
    int Id,
    string Name,
    string Type,
    decimal Price,
    int SessionCount,
    string? Description,
    string? CoverImagePath);

/// <summary>
/// GET /api/org/intents 列表项：{id,name,phone,productId,productName?,message,
/// status,followedByStaffId,followedByName?,createdAt}。status 为枚举名字符串
/// （New/Followed/Converted/Invalid）；productName/followedByName 缺失为 null。
/// </summary>
public record IntentListItem(
    int Id,
    string? Name,
    string Phone,
    int? ProductId,
    string? ProductName,
    string? Message,
    string Status,
    int? FollowedByStaffId,
    string? FollowedByName,
    DateTime CreatedAt);

/// <summary>
/// POST /api/org/intents/{id}/mark-converted 的可选请求体 {orderId?}。
/// PurchaseIntent 没有 OrderId 列：orderId 被接受但有意不持久化（YAGNI，
/// 后续接订单闭环时再考虑加列/关联）。
/// </summary>
public record MarkConvertedRequest(int? OrderId);

/// <summary>
/// POST /api/org/intents/{id}/mark-invalid 的可选请求体 {reason?}。
/// 与 orderId 同理：接受但不持久化（当前无 reason 列）。
/// </summary>
public record MarkInvalidRequest(string? Reason);
